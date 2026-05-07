using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Artifacts.Linux;

public sealed class LinuxArtifactSidecar
{
    private readonly Func<ProcessStartInfo> _factory;
    private readonly ILogger _logger;

    public LinuxArtifactSidecar(Func<ProcessStartInfo> factory, ILogger? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? NullLogger.Instance;
    }

    public static ProcessStartInfo DefaultSidecar(string parsersDir) => new()
    {
        FileName = OperatingSystem.IsWindows() ? "python.exe" : "python3",
        ArgumentList = { "-m", "linux.linux_worker" },
        WorkingDirectory = parsersDir,
    };

    private static DateTimeOffset? Ts(JsonNode? n)
    {
        var s = n?.GetValue<string?>();
        return string.IsNullOrEmpty(s) ? null
            : DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto) ? dto : null;
    }

    private async Task<JsonObject> InvokeAsync(string method, JsonObject args, CancellationToken ct)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var resp = await sc.InvokeAsync(method, args, ct).ConfigureAwait(false);
        return (resp as JsonObject) ?? new JsonObject();
    }

    public async IAsyncEnumerable<ShellHistoryEntry> ReadShellHistoryAsync(string rootPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("shell_history", new JsonObject { ["root"] = rootPath }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new ShellHistoryEntry(
                r["user"]?.GetValue<string>() ?? "?",
                r["shell"]?.GetValue<string>() ?? "?",
                r["command"]?.GetValue<string>() ?? "",
                Ts(r["timestamp"]));
        }
    }

    public async IAsyncEnumerable<AuthLogEntry> ReadAuthLogAsync(string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("auth_log", new JsonObject { ["path"] = path }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new AuthLogEntry(
                Ts(r["timestamp"]) ?? DateTimeOffset.MinValue,
                r["host"]?.GetValue<string>() ?? "",
                r["process"]?.GetValue<string>() ?? "",
                r["message"]?.GetValue<string>() ?? "",
                r["user"]?.GetValue<string?>(),
                r["remote_host"]?.GetValue<string?>());
        }
    }

    public async IAsyncEnumerable<JournalctlEntry> ReadJournalAsync(string journalDir, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("journalctl", new JsonObject { ["journal_dir"] = journalDir }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new JournalctlEntry(
                Ts(r["timestamp"]) ?? DateTimeOffset.MinValue,
                r["unit"]?.GetValue<string>() ?? "",
                r["priority"]?.GetValue<string>() ?? "",
                r["message"]?.GetValue<string>() ?? "",
                r["user"]?.GetValue<string?>());
        }
    }

    public async IAsyncEnumerable<CronEntry> ReadCronAsync(string rootPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("cron", new JsonObject { ["root"] = rootPath }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new CronEntry(
                r["user"]?.GetValue<string>() ?? "?",
                r["schedule"]?.GetValue<string>() ?? "",
                r["command"]?.GetValue<string>() ?? "",
                r["source"]?.GetValue<string>() ?? "");
        }
    }

    public async IAsyncEnumerable<SshKnownHost> ReadSshKnownHostsAsync(string rootPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("ssh_known_hosts", new JsonObject { ["root"] = rootPath }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new SshKnownHost(
                r["user"]?.GetValue<string>() ?? "?",
                r["host"]?.GetValue<string>() ?? "",
                r["key_type"]?.GetValue<string>() ?? "",
                r["fingerprint"]?.GetValue<string>() ?? "");
        }
    }

    public async IAsyncEnumerable<TrashEntry> ReadTrashAsync(string rootPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("trash", new JsonObject { ["root"] = rootPath }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new TrashEntry(
                r["user"]?.GetValue<string>() ?? "?",
                r["original_path"]?.GetValue<string>() ?? "",
                r["size"]?.GetValue<long>() ?? 0,
                Ts(r["deleted_at"]) ?? DateTimeOffset.MinValue);
        }
    }

    public async IAsyncEnumerable<PackageLogEntry> ReadPackageLogsAsync(string rootPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("package_logs", new JsonObject { ["root"] = rootPath }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new PackageLogEntry(
                Ts(r["timestamp"]) ?? DateTimeOffset.MinValue,
                r["pm"]?.GetValue<string>() ?? "?",
                r["action"]?.GetValue<string>() ?? "",
                r["package"]?.GetValue<string>() ?? "",
                r["version"]?.GetValue<string?>());
        }
    }

    public async IAsyncEnumerable<SystemdUnit> ReadSystemdUnitsAsync(string rootPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await InvokeAsync("systemd_units", new JsonObject { ["root"] = rootPath }, ct).ConfigureAwait(false);
        foreach (var r in (resp["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new SystemdUnit(
                r["name"]?.GetValue<string>() ?? "",
                r["path"]?.GetValue<string>() ?? "",
                r["enabled"]?.GetValue<bool>() ?? false,
                r["masked"]?.GetValue<bool>() ?? false,
                r["state"]?.GetValue<string?>());
        }
    }
}
