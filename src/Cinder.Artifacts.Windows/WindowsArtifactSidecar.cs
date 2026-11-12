using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cinder.Core.Diagnostics;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Artifacts.Windows;

/// <summary>
/// Single dispatcher to <c>parsers/windows/win_worker.py</c> which routes per-artifact requests
/// to regipy / python-evtx / direct .pf parsing / direct .lnk parsing / SQLite (browser) /
/// libesedb (SRUM/Amcache when ese parsing is needed).
/// <para>
/// Values the worker could not determine arrive as null and are passed on as null: a timestamp
/// is never replaced by a minimum date and a visit count is never replaced by 1. Where a parser
/// reports what it left out (<see cref="LoadPrefetchAsync"/>, <see cref="LoadBrowserHistoryAsync"/>)
/// that report is part of the result.
/// </para>
/// </summary>
public sealed class WindowsArtifactSidecar
{
    private const int EvtxPageSize = 1000;

    private readonly Func<ISidecarChannel> _open;
    private readonly ILogger _logger;

    public WindowsArtifactSidecar(Func<ProcessStartInfo> factory, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _logger = logger ?? NullLogger.Instance;
        _open = () => new SidecarClient(factory(), _logger);
    }

    /// <summary>Uses an already-built channel factory (tests, or a host that pools sidecars).</summary>
    public WindowsArtifactSidecar(Func<ISidecarChannel> channelFactory, ILogger? logger = null)
    {
        _open = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _logger = logger ?? NullLogger.Instance;
    }

    public static ProcessStartInfo DefaultSidecar(string parsersDir) => new()
    {
        FileName = ExecutableResolver.ResolveRequired(
            OperatingSystem.IsWindows() ? "python.exe" : "python3",
            "Install Python 3.12+ and make sure it is on PATH."),
        ArgumentList = { "-m", "windows.win_worker" },
        WorkingDirectory = parsersDir,
    };

    private async Task<JsonObject> InvokeAsync(string method, JsonObject args, CancellationToken ct)
    {
        await using var sc = _open();
        var resp = await sc.InvokeAsync(method, args, null, null, ct).ConfigureAwait(false);
        return (resp as JsonObject) ?? new JsonObject();
    }

    // The worker derives every timestamp below from a FILETIME, an epoch or a registry key
    // time and writes it with an explicit +00:00, so these are instants.
    private static DateTimeOffset? Ts(JsonNode? n) => SidecarJson.Instant(n);

    private static string Str(JsonNode? n, string fallback = "") => SidecarJson.String(n) ?? fallback;

    private static TimeSpan? Milliseconds(JsonNode? n)
    {
        if (SidecarJson.Int64(n) is not { } ms)
        {
            return null;
        }
        try
        {
            return TimeSpan.FromMilliseconds(ms);
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException)
        {
            return null; // longer than a TimeSpan can hold: not a duration anyone measured
        }
    }

    public async IAsyncEnumerable<UserAssistEntry> ReadUserAssistAsync(string ntuserPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("registry_userassist", new JsonObject { ["ntuser_path"] = ntuserPath }, ct).ConfigureAwait(false);
        foreach (var r in SidecarJson.Objects(resp["entries"]))
        {
            yield return new UserAssistEntry(
                Str(r["user"]),
                Str(r["program"]),
                SidecarJson.Int32(r["run_count"]) ?? 0,
                Milliseconds(r["focus_ms"]),
                Ts(r["last_executed"]));
        }
    }

    public async IAsyncEnumerable<ShimCacheEntry> ReadShimCacheAsync(string systemHive, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("registry_shimcache", new JsonObject { ["system_hive"] = systemHive }, ct).ConfigureAwait(false);
        foreach (var r in SidecarJson.Objects(resp["entries"]))
        {
            yield return new ShimCacheEntry(Str(r["path"]), Ts(r["modified"]), SidecarJson.Boolean(r["executed"]) ?? false);
        }
    }

    public async IAsyncEnumerable<AmcacheEntry> ReadAmcacheAsync(string amcacheHive, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("registry_amcache", new JsonObject { ["amcache_hive"] = amcacheHive }, ct).ConfigureAwait(false);
        foreach (var r in SidecarJson.Objects(resp["entries"]))
        {
            yield return new AmcacheEntry(Str(r["path"]),
                SidecarJson.String(r["sha1"]), Ts(r["first_seen"]), SidecarJson.String(r["publisher"]));
        }
    }

    public async IAsyncEnumerable<UsbDeviceArtifact> ReadUsbAsync(string systemHive, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("registry_usb", new JsonObject { ["system_hive"] = systemHive }, ct).ConfigureAwait(false);
        foreach (var r in SidecarJson.Objects(resp["entries"]))
        {
            yield return new UsbDeviceArtifact(
                Str(r["device_id"]),
                Str(r["friendly_name"]),
                Ts(r["first_connected"]), Ts(r["last_connected"]),
                SidecarJson.String(r["serial"]));
        }
    }

    public async IAsyncEnumerable<WifiNetworkArtifact> ReadWifiAsync(string softwareHive, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("registry_wifi", new JsonObject { ["software_hive"] = softwareHive }, ct).ConfigureAwait(false);
        foreach (var r in SidecarJson.Objects(resp["entries"]))
        {
            yield return new WifiNetworkArtifact(
                Str(r["ssid"]),
                Ts(r["first_seen"]), Ts(r["last_seen"]),
                SidecarJson.String(r["auth"]));
        }
    }

    /// <summary>
    /// Parses a Prefetch directory and reports the files that were not parsed alongside the
    /// ones that were (see <see cref="PrefetchResult"/>).
    /// </summary>
    public async Task<PrefetchResult> LoadPrefetchAsync(string prefetchDir, CancellationToken ct = default)
    {
        var resp = await InvokeAsync("prefetch", new JsonObject { ["prefetch_dir"] = prefetchDir }, ct).ConfigureAwait(false);
        var entries = new List<PrefetchEntry>();
        foreach (var r in SidecarJson.Objects(resp["entries"]))
        {
            var times = new List<DateTimeOffset>();
            if (r["all_run_times"] is JsonArray runTimes)
            {
                foreach (var t in runTimes)
                {
                    if (Ts(t) is { } time)
                    {
                        times.Add(time);
                    }
                }
            }
            entries.Add(new PrefetchEntry(
                Str(r["executable"]),
                Str(r["hash"]),
                SidecarJson.Int32(r["run_count"]) ?? 0,
                Ts(r["last_run"]),
                times,
                SidecarJson.Strings(r["loaded_files"]),
                FormatVersion: SidecarJson.Int32(r["version"]),
                SourceFile: SidecarJson.String(r["source_file"])));
        }
        var skipped = SidecarJson.Objects(resp["skipped"])
            .Select(s => new SkippedPrefetchFile(Str(s["file"]), Str(s["reason"])))
            .ToList();
        var compressed = SidecarJson.Int32(resp["compressed_not_decoded"])
            ?? skipped.Count(s => s.Reason.StartsWith("compressed", StringComparison.OrdinalIgnoreCase));
        if (skipped.Count > 0)
        {
            _logger.LogWarning(
                "Prefetch: {Parsed} file(s) parsed, {Skipped} not parsed ({Compressed} MAM-compressed, not decoded).",
                entries.Count, skipped.Count, compressed);
        }
        return new PrefetchResult(entries, skipped, compressed);
    }

    /// <summary>
    /// Rows only. Files the parser skipped (every MAM-compressed file, on Windows 10+) are
    /// logged, not returned: use <see cref="LoadPrefetchAsync"/> wherever an empty or short
    /// listing would otherwise be read as "nothing ran".
    /// </summary>
    public async IAsyncEnumerable<PrefetchEntry> ReadPrefetchAsync(string prefetchDir, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entry in (await LoadPrefetchAsync(prefetchDir, ct).ConfigureAwait(false)).Entries)
        {
            yield return entry;
        }
    }

    public async IAsyncEnumerable<ShellbagEntry> ReadShellbagsAsync(string ntuserPath, string user, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("shellbags", new JsonObject { ["ntuser_path"] = ntuserPath, ["user"] = user }, ct).ConfigureAwait(false);
        foreach (var r in SidecarJson.Objects(resp["entries"]))
        {
            yield return new ShellbagEntry(
                Str(r["user"], user),
                Str(r["path"]),
                Ts(r["first_accessed"]), Ts(r["last_accessed"]),
                SidecarJson.Int32(r["access_count"]) ?? 0);
        }
    }

    public async IAsyncEnumerable<JumplistEntry> ReadJumplistsAsync(string jumplistDir, string user, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("jumplists", new JsonObject { ["jumplist_dir"] = jumplistDir, ["user"] = user }, ct).ConfigureAwait(false);
        foreach (var r in SidecarJson.Objects(resp["entries"]))
        {
            yield return new JumplistEntry(
                Str(r["user"], user),
                Str(r["app_id"]),
                Str(r["target_path"]),
                Ts(r["access_time"]));
        }
    }

    public async Task<LnkEntry?> ReadLnkAsync(string path, CancellationToken ct = default)
    {
        var resp = await InvokeAsync("lnk", new JsonObject { ["path"] = path }, ct).ConfigureAwait(false);
        if (SidecarJson.String(resp["target_path"]) is not { } target)
        {
            return null;
        }
        return new LnkEntry(
            path, target,
            SidecarJson.String(resp["arguments"]),
            SidecarJson.String(resp["icon"]),
            SidecarJson.String(resp["working_dir"]),
            Ts(resp["target_created"]), Ts(resp["target_modified"]), Ts(resp["target_accessed"]),
            SidecarJson.String(resp["volume_serial"]),
            SidecarJson.String(resp["machine_id"]));
    }

    public async IAsyncEnumerable<EventLogRecord> ReadEvtxAsync(string evtxPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = _open();
        long cursor = 0;
        while (true)
        {
            var resp = await sc.InvokeAsync("evtx_page",
                new JsonObject { ["path"] = evtxPath, ["cursor"] = cursor, ["limit"] = EvtxPageSize }, null, null, ct).ConfigureAwait(false);
            if (resp is not JsonObject o || o["entries"] is not JsonArray rows)
            {
                yield break;
            }
            foreach (var r in rows.OfType<JsonObject>())
            {
                yield return new EventLogRecord(
                    SidecarJson.Int64(r["record_id"]) ?? 0,
                    SidecarJson.Int32(r["event_id"]) ?? 0,
                    Str(r["provider"]),
                    Str(r["channel"]),
                    Str(r["computer"]),
                    SidecarJson.String(r["user"]),
                    // TimeCreated/@SystemTime is UTC by definition, though it is written without
                    // an offset. Null when absent or unparseable: never a minimum date.
                    SidecarJson.Instant(r["timestamp"], utcByDefinition: true),
                    Str(r["level"]),
                    Str(r["summary"]));
            }
            if (rows.Count < EvtxPageSize)
            {
                yield break;
            }
            cursor += rows.Count;
        }
    }

    /// <summary>
    /// Reads a browser history database and reports what could not be read from it
    /// (see <see cref="BrowserHistoryResult"/>).
    /// </summary>
    public async Task<BrowserHistoryResult> LoadBrowserHistoryAsync(string profilePath, string browser, string user, CancellationToken ct = default)
    {
        var resp = await InvokeAsync("browser_history",
            new JsonObject { ["profile_path"] = profilePath, ["browser"] = browser, ["user"] = user }, ct).ConfigureAwait(false);
        var entries = new List<BrowserHistoryEntry>();
        foreach (var r in SidecarJson.Objects(resp["entries"]))
        {
            entries.Add(new BrowserHistoryEntry(
                Str(r["user"], user),
                Str(r["browser"], browser),
                Str(r["url"]),
                SidecarJson.String(r["title"]),
                SidecarJson.Int32(r["visit_count"]),
                Ts(r["timestamp"]),
                SidecarJson.String(r["visit_type"])));
        }
        var result = new BrowserHistoryResult(
            entries,
            RowErrors: SidecarJson.Int32(resp["row_errors"]) ?? 0,
            TimestampErrors: SidecarJson.Int32(resp["timestamp_errors"]) ?? 0,
            ReadError: SidecarJson.String(resp["read_error"]));
        if (!result.IsComplete)
        {
            _logger.LogWarning(
                "Browser history ({Browser}): {Rows} row(s) read, {RowErrors} unreadable, {TimestampErrors} with an invalid timestamp; read error: {ReadError}.",
                browser, entries.Count, result.RowErrors, result.TimestampErrors, result.ReadError ?? "none");
        }
        return result;
    }

    /// <summary>Rows only; errors are logged. Prefer <see cref="LoadBrowserHistoryAsync"/>.</summary>
    public async IAsyncEnumerable<BrowserHistoryEntry> ReadBrowserHistoryAsync(string profilePath, string browser, string user, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entry in (await LoadBrowserHistoryAsync(profilePath, browser, user, ct).ConfigureAwait(false)).Entries)
        {
            yield return entry;
        }
    }

    public async IAsyncEnumerable<SrumApplicationUsage> ReadSrumApplicationsAsync(string srudbPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("srum_applications", new JsonObject { ["srudb_path"] = srudbPath }, ct).ConfigureAwait(false);
        foreach (var r in SidecarJson.Objects(resp["entries"]))
        {
            yield return new SrumApplicationUsage(
                Str(r["user"]),
                Str(r["application"]),
                Ts(r["timestamp"]),
                SidecarJson.Int64(r["fg_cpu_ms"]) ?? 0,
                SidecarJson.Int64(r["bytes_read"]) ?? 0,
                SidecarJson.Int64(r["bytes_written"]) ?? 0);
        }
    }
}
