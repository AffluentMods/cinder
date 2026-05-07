using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Artifacts.Windows;

/// <summary>
/// Single dispatcher to <c>parsers/windows/win_worker.py</c> which routes per-artifact requests
/// to regipy / python-evtx / direct .pf parsing / direct .lnk parsing / SQLite (browser) /
/// libesedb (SRUM/Amcache when ese parsing is needed).
/// </summary>
public sealed class WindowsArtifactSidecar
{
    private readonly Func<ProcessStartInfo> _factory;
    private readonly ILogger _logger;

    public WindowsArtifactSidecar(Func<ProcessStartInfo> factory, ILogger? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? NullLogger.Instance;
    }

    public static ProcessStartInfo DefaultSidecar(string parsersDir) => new()
    {
        FileName = OperatingSystem.IsWindows() ? "python.exe" : "python3",
        ArgumentList = { "-m", "windows.win_worker" },
        WorkingDirectory = parsersDir,
    };

    private async Task<JsonObject> InvokeAsync(string method, JsonObject args, CancellationToken ct)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var resp = await sc.InvokeAsync(method, args, ct).ConfigureAwait(false);
        return (resp as JsonObject) ?? new JsonObject();
    }

    private static DateTimeOffset? Ts(JsonNode? n)
    {
        var s = n?.GetValue<string?>();
        return string.IsNullOrEmpty(s) ? null
            : DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto) ? dto : null;
    }

    public async IAsyncEnumerable<UserAssistEntry> ReadUserAssistAsync(string ntuserPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("registry_userassist", new JsonObject { ["ntuser_path"] = ntuserPath }, ct).ConfigureAwait(false);
        if (resp["entries"] is not JsonArray rows)
        {
            yield break;
        }
        foreach (var r in rows.OfType<JsonObject>())
        {
            yield return new UserAssistEntry(
                r["user"]?.GetValue<string>() ?? "",
                r["program"]?.GetValue<string>() ?? "",
                r["run_count"]?.GetValue<int>() ?? 0,
                r["focus_ms"]?.GetValue<long?>() is long ms ? TimeSpan.FromMilliseconds(ms) : null,
                Ts(r["last_executed"]));
        }
    }

    public async IAsyncEnumerable<ShimCacheEntry> ReadShimCacheAsync(string systemHive, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("registry_shimcache", new JsonObject { ["system_hive"] = systemHive }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new ShimCacheEntry(r["path"]?.GetValue<string>() ?? "", Ts(r["modified"]), r["executed"]?.GetValue<bool>() ?? false);
        }
    }

    public async IAsyncEnumerable<AmcacheEntry> ReadAmcacheAsync(string amcacheHive, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("registry_amcache", new JsonObject { ["amcache_hive"] = amcacheHive }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new AmcacheEntry(r["path"]?.GetValue<string>() ?? "",
                r["sha1"]?.GetValue<string?>(), Ts(r["first_seen"]), r["publisher"]?.GetValue<string?>());
        }
    }

    public async IAsyncEnumerable<UsbDeviceArtifact> ReadUsbAsync(string systemHive, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("registry_usb", new JsonObject { ["system_hive"] = systemHive }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new UsbDeviceArtifact(
                r["device_id"]?.GetValue<string>() ?? "",
                r["friendly_name"]?.GetValue<string>() ?? "",
                Ts(r["first_connected"]), Ts(r["last_connected"]),
                r["serial"]?.GetValue<string?>());
        }
    }

    public async IAsyncEnumerable<WifiNetworkArtifact> ReadWifiAsync(string softwareHive, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("registry_wifi", new JsonObject { ["software_hive"] = softwareHive }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new WifiNetworkArtifact(
                r["ssid"]?.GetValue<string>() ?? "",
                Ts(r["first_seen"]), Ts(r["last_seen"]),
                r["auth"]?.GetValue<string?>());
        }
    }

    public async IAsyncEnumerable<PrefetchEntry> ReadPrefetchAsync(string prefetchDir, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("prefetch", new JsonObject { ["prefetch_dir"] = prefetchDir }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var times = (r["all_run_times"] as JsonArray)?
                .Select(t => Ts(t)).Where(t => t.HasValue).Select(t => t!.Value).ToList() ?? [];
            var loaded = (r["loaded_files"] as JsonArray)?
                .Select(t => t?.GetValue<string>() ?? "").ToList() ?? [];
            yield return new PrefetchEntry(
                r["executable"]?.GetValue<string>() ?? "",
                r["hash"]?.GetValue<string>() ?? "",
                r["run_count"]?.GetValue<int>() ?? 0,
                Ts(r["last_run"]),
                times, loaded);
        }
    }

    public async IAsyncEnumerable<ShellbagEntry> ReadShellbagsAsync(string ntuserPath, string user, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("shellbags", new JsonObject { ["ntuser_path"] = ntuserPath, ["user"] = user }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new ShellbagEntry(
                r["user"]?.GetValue<string>() ?? user,
                r["path"]?.GetValue<string>() ?? "",
                Ts(r["first_accessed"]), Ts(r["last_accessed"]),
                r["access_count"]?.GetValue<int>() ?? 0);
        }
    }

    public async IAsyncEnumerable<JumplistEntry> ReadJumplistsAsync(string jumplistDir, string user, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("jumplists", new JsonObject { ["jumplist_dir"] = jumplistDir, ["user"] = user }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new JumplistEntry(
                r["user"]?.GetValue<string>() ?? user,
                r["app_id"]?.GetValue<string>() ?? "",
                r["target_path"]?.GetValue<string>() ?? "",
                Ts(r["access_time"]));
        }
    }

    public async Task<LnkEntry?> ReadLnkAsync(string path, CancellationToken ct = default)
    {
        var resp = await InvokeAsync("lnk", new JsonObject { ["path"] = path }, ct).ConfigureAwait(false);
        if (resp["target_path"]?.GetValue<string?>() is not { } target)
        {
            return null;
        }
        return new LnkEntry(
            path, target,
            resp["arguments"]?.GetValue<string?>(),
            resp["icon"]?.GetValue<string?>(),
            resp["working_dir"]?.GetValue<string?>(),
            Ts(resp["target_created"]), Ts(resp["target_modified"]), Ts(resp["target_accessed"]),
            resp["volume_serial"]?.GetValue<string?>(),
            resp["machine_id"]?.GetValue<string?>());
    }

    public async IAsyncEnumerable<EventLogRecord> ReadEvtxAsync(string evtxPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        long cursor = 0;
        while (true)
        {
            var resp = await sc.InvokeAsync("evtx_page",
                new JsonObject { ["path"] = evtxPath, ["cursor"] = cursor, ["limit"] = 1000 }, ct).ConfigureAwait(false);
            if (resp is not JsonObject o || o["entries"] is not JsonArray rows)
            {
                yield break;
            }
            foreach (var r in rows.OfType<JsonObject>())
            {
                yield return new EventLogRecord(
                    r["record_id"]?.GetValue<long>() ?? 0,
                    r["event_id"]?.GetValue<int>() ?? 0,
                    r["provider"]?.GetValue<string>() ?? "",
                    r["channel"]?.GetValue<string>() ?? "",
                    r["computer"]?.GetValue<string>() ?? "",
                    r["user"]?.GetValue<string?>(),
                    Ts(r["timestamp"]) ?? DateTimeOffset.MinValue,
                    r["level"]?.GetValue<string>() ?? "",
                    r["summary"]?.GetValue<string>() ?? "");
            }
            if (rows.Count < 1000)
            {
                yield break;
            }
            cursor += rows.Count;
        }
    }

    public async IAsyncEnumerable<BrowserHistoryEntry> ReadBrowserHistoryAsync(string profilePath, string browser, string user, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("browser_history",
            new JsonObject { ["profile_path"] = profilePath, ["browser"] = browser, ["user"] = user }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new BrowserHistoryEntry(
                r["user"]?.GetValue<string>() ?? user,
                r["browser"]?.GetValue<string>() ?? browser,
                r["url"]?.GetValue<string>() ?? "",
                r["title"]?.GetValue<string?>(),
                r["visit_count"]?.GetValue<int>() ?? 1,
                Ts(r["timestamp"]) ?? DateTimeOffset.MinValue,
                r["visit_type"]?.GetValue<string?>());
        }
    }

    public async IAsyncEnumerable<SrumApplicationUsage> ReadSrumApplicationsAsync(string srudbPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("srum_applications", new JsonObject { ["srudb_path"] = srudbPath }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new SrumApplicationUsage(
                r["user"]?.GetValue<string>() ?? "",
                r["application"]?.GetValue<string>() ?? "",
                Ts(r["timestamp"]) ?? DateTimeOffset.MinValue,
                r["fg_cpu_ms"]?.GetValue<long>() ?? 0,
                r["bytes_read"]?.GetValue<long>() ?? 0,
                r["bytes_written"]?.GetValue<long>() ?? 0);
        }
    }
}
