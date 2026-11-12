using System.Diagnostics;
using System.Text.Json.Nodes;
using Cinder.Core.Diagnostics;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Artifacts.Linux;

/// <summary>
/// C# face of <c>parsers/linux/linux_worker.py</c>.
/// <para>
/// Every artifact has two entry points. <c>Load…Async</c> returns a
/// <see cref="LinuxArtifactResult{T}"/>: the rows together with what the parser refused to read
/// (symlinks leaving the evidence root), what it truncated, and how many lines it could not
/// parse. <c>Read…Async</c> streams just the rows and logs a warning for the rest; use it only
/// where an incomplete listing does not need to be shown as such.
/// </para>
/// <para>
/// Nothing here invents a value. A timestamp the parser reported as null stays null, and a time
/// whose zone the log does not record is flagged rather than passed off as UTC (see
/// <c>LinuxArtifacts.cs</c>).
/// </para>
/// </summary>
public sealed class LinuxArtifactSidecar
{
    private readonly Func<ISidecarChannel> _open;
    private readonly ILogger _logger;

    public LinuxArtifactSidecar(Func<ProcessStartInfo> factory, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _logger = logger ?? NullLogger.Instance;
        _open = () => new SidecarClient(factory(), _logger);
    }

    /// <summary>Uses an already-built channel factory (tests, or a host that pools sidecars).</summary>
    public LinuxArtifactSidecar(Func<ISidecarChannel> channelFactory, ILogger? logger = null)
    {
        _open = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _logger = logger ?? NullLogger.Instance;
    }

    public static ProcessStartInfo DefaultSidecar(string parsersDir) => new()
    {
        FileName = ExecutableResolver.ResolveRequired(
            OperatingSystem.IsWindows() ? "python.exe" : "python3",
            "Install Python 3.12+ and make sure it is on PATH."),
        ArgumentList = { "-m", "linux.linux_worker" },
        WorkingDirectory = parsersDir,
    };

    private async Task<JsonObject> InvokeAsync(string method, JsonObject args, CancellationToken ct)
    {
        await using var sc = _open();
        var resp = await sc.InvokeAsync(method, args, null, null, ct).ConfigureAwait(false);
        return (resp as JsonObject) ?? new JsonObject();
    }

    private LinuxArtifactResult<T> Result<T>(string method, JsonObject resp, Func<JsonObject, T> map)
    {
        var entries = new List<T>();
        foreach (var row in SidecarJson.Objects(resp["entries"]))
        {
            entries.Add(map(row));
        }
        var refused = SidecarJson.Objects(resp["refused"])
            .Select(r => new RefusedPath(SidecarJson.String(r["path"]) ?? "", SidecarJson.String(r["reason"]) ?? ""))
            .ToList();
        var result = new LinuxArtifactResult<T>(entries, refused, SidecarJson.Strings(resp["truncated"]))
        {
            UnparsedLines = (SidecarJson.Int32(resp["unparsed_lines"]) ?? 0) + (SidecarJson.Int32(resp["malformed_lines"]) ?? 0),
            TruncatedBy = SidecarJson.String(resp["truncated_by"]),
            YearSource = SidecarJson.String(resp["year_source"]),
            YearReference = SidecarJson.Instant(resp["year_reference"]),
            Note = SidecarJson.String(resp["todo"]) ?? SidecarJson.String(resp["stderr"]),
        };
        if (!result.IsComplete)
        {
            _logger.LogWarning(
                "Linux parser '{Method}' returned an incomplete listing: {Refused} path(s) refused, {Truncated} file(s) truncated, " +
                "{Unparsed} line(s) unparsed, truncated by {TruncatedBy}.",
                method, result.Refused.Count, result.Truncated.Count, result.UnparsedLines, result.TruncatedBy ?? "nothing");
        }
        return result;
    }

    private static async IAsyncEnumerable<T> RowsAsync<T>(Task<LinuxArtifactResult<T>> load)
    {
        foreach (var entry in (await load.ConfigureAwait(false)).Entries)
        {
            yield return entry;
        }
    }

    // ---------------- shell history ----------------

    public async Task<LinuxArtifactResult<ShellHistoryEntry>> LoadShellHistoryAsync(string rootPath, CancellationToken ct = default)
    {
        var resp = await InvokeAsync("shell_history", new JsonObject { ["root"] = rootPath }, ct).ConfigureAwait(false);
        return Result("shell_history", resp, r => new ShellHistoryEntry(
            SidecarJson.String(r["user"]) ?? "?",
            SidecarJson.String(r["shell"]) ?? "?",
            SidecarJson.String(r["command"]) ?? "",
            SidecarJson.Instant(r["timestamp"])));
    }

    public IAsyncEnumerable<ShellHistoryEntry> ReadShellHistoryAsync(string rootPath, CancellationToken ct = default)
        => RowsAsync(LoadShellHistoryAsync(rootPath, ct));

    // ---------------- auth.log ----------------

    /// <summary>Parses an auth.log / secure file.</summary>
    /// <param name="path">The log file.</param>
    /// <param name="rootPath">
    /// The evidence root the log must stay inside. Pass it whenever the log comes from a mounted
    /// image: without it the parser can only hold the file to its own directory, which does not
    /// catch a symlinked parent directory.
    /// </param>
    /// <param name="referenceMtime">
    /// When the log was last written, if the caller knows it from the image. Classic syslog
    /// lines carry no year; it is inferred from this, or else from the file's own modification
    /// time (which is wrong for a copy that did not preserve timestamps).
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public async Task<LinuxArtifactResult<AuthLogEntry>> LoadAuthLogAsync(
        string path, string? rootPath, DateTimeOffset? referenceMtime = null, CancellationToken ct = default)
    {
        var args = new JsonObject { ["path"] = path };
        if (!string.IsNullOrEmpty(rootPath))
        {
            args["root"] = rootPath;
        }
        if (referenceMtime is { } mtime)
        {
            args["reference_mtime"] = mtime.ToUnixTimeMilliseconds() / 1000.0;
        }
        var resp = await InvokeAsync("auth_log", args, ct).ConfigureAwait(false);
        return Result("auth_log", resp, r =>
        {
            var time = SidecarJson.Time(r["timestamp"]);
            return new AuthLogEntry(
                time?.Value,
                SidecarJson.String(r["host"]) ?? "",
                SidecarJson.String(r["process"]) ?? "",
                SidecarJson.String(r["message"]) ?? "",
                SidecarJson.String(r["user"]),
                SidecarJson.String(r["remote_host"]),
                // Known only if the text carried an offset and the parser did not say otherwise.
                TimeZoneKnown: time is { ZoneKnown: true } && SidecarJson.Boolean(r["tz_known"]) != false,
                YearInferred: SidecarJson.Boolean(r["year_inferred"]) ?? false,
                TimestampRaw: SidecarJson.String(r["timestamp_raw"]));
        });
    }

    public IAsyncEnumerable<AuthLogEntry> ReadAuthLogAsync(string path, string? rootPath, CancellationToken ct = default)
        => RowsAsync(LoadAuthLogAsync(path, rootPath, null, ct));

    /// <summary>
    /// Parses an auth.log with no evidence root to confine it to (the parser then uses the
    /// file's own directory). Prefer <see cref="ReadAuthLogAsync(string, string, CancellationToken)"/>.
    /// </summary>
    public IAsyncEnumerable<AuthLogEntry> ReadAuthLogAsync(string path, CancellationToken ct = default)
        => RowsAsync(LoadAuthLogAsync(path, null, null, ct));

    // ---------------- journal ----------------

    /// <summary>Reads a systemd journal directory through <c>journalctl</c>.</summary>
    /// <param name="journalDir">Directory holding the journal files.</param>
    /// <param name="rootPath">The evidence root the directory must stay inside (see <see cref="LoadAuthLogAsync"/>).</param>
    /// <param name="limit">Maximum number of records; the parser's own cap when null.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<LinuxArtifactResult<JournalctlEntry>> LoadJournalAsync(
        string journalDir, string? rootPath, int? limit = null, CancellationToken ct = default)
    {
        var args = new JsonObject { ["journal_dir"] = journalDir };
        if (!string.IsNullOrEmpty(rootPath))
        {
            args["root"] = rootPath;
        }
        if (limit is { } max)
        {
            args["limit"] = max;
        }
        var resp = await InvokeAsync("journalctl", args, ct).ConfigureAwait(false);
        return Result("journalctl", resp, r => new JournalctlEntry(
            SidecarJson.Instant(r["timestamp"]),
            SidecarJson.String(r["unit"]) ?? "",
            SidecarJson.String(r["priority"]) ?? "",
            // journalctl emits a non-UTF-8 MESSAGE as an array of byte values, not a string.
            SidecarJson.String(r["message"]) ?? (r["message"] is JsonArray bytes ? $"<{bytes.Count} bytes, not text>" : ""),
            SidecarJson.String(r["user"])));
    }

    public IAsyncEnumerable<JournalctlEntry> ReadJournalAsync(string journalDir, string? rootPath, CancellationToken ct = default)
        => RowsAsync(LoadJournalAsync(journalDir, rootPath, null, ct));

    /// <summary>
    /// Reads a journal directory with no evidence root to confine it to. Prefer
    /// <see cref="ReadJournalAsync(string, string, CancellationToken)"/>.
    /// </summary>
    public IAsyncEnumerable<JournalctlEntry> ReadJournalAsync(string journalDir, CancellationToken ct = default)
        => RowsAsync(LoadJournalAsync(journalDir, null, null, ct));

    // ---------------- cron ----------------

    public async Task<LinuxArtifactResult<CronEntry>> LoadCronAsync(string rootPath, CancellationToken ct = default)
    {
        var resp = await InvokeAsync("cron", new JsonObject { ["root"] = rootPath }, ct).ConfigureAwait(false);
        return Result("cron", resp, r => new CronEntry(
            SidecarJson.String(r["user"]) ?? "?",
            SidecarJson.String(r["schedule"]) ?? "",
            SidecarJson.String(r["command"]) ?? "",
            SidecarJson.String(r["source"]) ?? ""));
    }

    public IAsyncEnumerable<CronEntry> ReadCronAsync(string rootPath, CancellationToken ct = default)
        => RowsAsync(LoadCronAsync(rootPath, ct));

    // ---------------- ssh ----------------

    public async Task<LinuxArtifactResult<SshKnownHost>> LoadSshKnownHostsAsync(string rootPath, CancellationToken ct = default)
    {
        var resp = await InvokeAsync("ssh_known_hosts", new JsonObject { ["root"] = rootPath }, ct).ConfigureAwait(false);
        return Result("ssh_known_hosts", resp, r => new SshKnownHost(
            SidecarJson.String(r["user"]) ?? "?",
            SidecarJson.String(r["host"]) ?? "",
            SidecarJson.String(r["key_type"]) ?? "",
            SidecarJson.String(r["fingerprint"]),
            SidecarJson.String(r["marker"])));
    }

    public IAsyncEnumerable<SshKnownHost> ReadSshKnownHostsAsync(string rootPath, CancellationToken ct = default)
        => RowsAsync(LoadSshKnownHostsAsync(rootPath, ct));

    // ---------------- trash ----------------

    public async Task<LinuxArtifactResult<TrashEntry>> LoadTrashAsync(string rootPath, CancellationToken ct = default)
    {
        var resp = await InvokeAsync("trash", new JsonObject { ["root"] = rootPath }, ct).ConfigureAwait(false);
        return Result("trash", resp, r =>
        {
            var deleted = SidecarJson.Time(r["deleted_at"]);
            return new TrashEntry(
                SidecarJson.String(r["user"]) ?? "?",
                SidecarJson.String(r["original_path"]) ?? "",
                SidecarJson.Int64(r["size"]),
                deleted?.Value,
                TimeZoneKnown: deleted is { ZoneKnown: true });
        });
    }

    public IAsyncEnumerable<TrashEntry> ReadTrashAsync(string rootPath, CancellationToken ct = default)
        => RowsAsync(LoadTrashAsync(rootPath, ct));

    // ---------------- package manager logs ----------------

    public async Task<LinuxArtifactResult<PackageLogEntry>> LoadPackageLogsAsync(string rootPath, CancellationToken ct = default)
    {
        var resp = await InvokeAsync("package_logs", new JsonObject { ["root"] = rootPath }, ct).ConfigureAwait(false);
        return Result("package_logs", resp, r =>
        {
            var time = SidecarJson.Time(r["timestamp"]);
            return new PackageLogEntry(
                time?.Value,
                SidecarJson.String(r["pm"]) ?? "?",
                SidecarJson.String(r["action"]) ?? "",
                SidecarJson.String(r["package"]) ?? "",
                SidecarJson.String(r["version"]),
                TimeZoneKnown: time is { ZoneKnown: true },
                PreviousVersion: SidecarJson.String(r["previous_version"]));
        });
    }

    public IAsyncEnumerable<PackageLogEntry> ReadPackageLogsAsync(string rootPath, CancellationToken ct = default)
        => RowsAsync(LoadPackageLogsAsync(rootPath, ct));

    // ---------------- systemd ----------------

    public async Task<LinuxArtifactResult<SystemdUnit>> LoadSystemdUnitsAsync(string rootPath, CancellationToken ct = default)
    {
        var resp = await InvokeAsync("systemd_units", new JsonObject { ["root"] = rootPath }, ct).ConfigureAwait(false);
        return Result("systemd_units", resp, r => new SystemdUnit(
            SidecarJson.String(r["name"]) ?? "",
            SidecarJson.String(r["path"]) ?? "",
            SidecarJson.Boolean(r["enabled"]) ?? false,
            SidecarJson.Boolean(r["masked"]) ?? false,
            SidecarJson.String(r["state"])));
    }

    public IAsyncEnumerable<SystemdUnit> ReadSystemdUnitsAsync(string rootPath, CancellationToken ct = default)
        => RowsAsync(LoadSystemdUnitsAsync(rootPath, ct));
}
