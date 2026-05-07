using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Mobile;

/// <summary>
/// Wraps <c>parsers/mobile/mobile_worker.py</c> which uses <c>iphone-backup-decrypt</c>
/// (iOS) and direct SQLite/protobuf parsing (Android ADB / MTP backups).
/// </summary>
public sealed class MobileBackupSidecar : IMobileBackupReader
{
    private readonly Func<ProcessStartInfo> _factory;
    private readonly ILogger _logger;

    public MobileBackupSidecar(Func<ProcessStartInfo> factory, ILogger? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? NullLogger.Instance;
    }

    public static ProcessStartInfo DefaultSidecar(string parsersDir) => new()
    {
        FileName = OperatingSystem.IsWindows() ? "python.exe" : "python3",
        ArgumentList = { "-m", "mobile.mobile_worker" },
        WorkingDirectory = parsersDir,
    };

    private static DateTimeOffset Ts(JsonNode? n)
        => DateTimeOffset.TryParse(n?.GetValue<string?>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto) ? dto : DateTimeOffset.MinValue;

    public async Task<MobileBackupInfo> InspectAsync(string backupPath, CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var resp = await sc.InvokeAsync("inspect", new JsonObject { ["path"] = backupPath }, ct).ConfigureAwait(false);
        var o = resp as JsonObject ?? new JsonObject();
        return new MobileBackupInfo(
            Platform: o["platform"]?.GetValue<string>() ?? "unknown",
            DeviceName: o["device"]?.GetValue<string>() ?? "",
            Os: o["os"]?.GetValue<string?>(),
            Created: o["created"]?.GetValue<string?>() is { } s ? Ts(s) : null,
            Encrypted: o["encrypted"]?.GetValue<bool>() ?? false);
    }

    public async IAsyncEnumerable<MobileMessage> MessagesAsync(string backupPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var resp = await sc.InvokeAsync("messages", new JsonObject { ["path"] = backupPath }, ct).ConfigureAwait(false);
        foreach (var r in ((resp as JsonObject)?["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new MobileMessage(
                r["source"]?.GetValue<string>() ?? "mobile.unknown",
                r["user"]?.GetValue<string?>(),
                r["chat_id"]?.GetValue<string>() ?? "",
                r["sender"]?.GetValue<string?>(),
                r["recipient"]?.GetValue<string?>(),
                r["body"]?.GetValue<string>() ?? "",
                Ts(r["timestamp"]),
                r["from_me"]?.GetValue<bool>() ?? false);
        }
    }

    public async IAsyncEnumerable<MobileCall> CallsAsync(string backupPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var resp = await sc.InvokeAsync("calls", new JsonObject { ["path"] = backupPath }, ct).ConfigureAwait(false);
        foreach (var r in ((resp as JsonObject)?["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new MobileCall(
                r["source"]?.GetValue<string>() ?? "mobile.calls",
                r["user"]?.GetValue<string?>(),
                r["direction"]?.GetValue<string>() ?? "?",
                r["number"]?.GetValue<string>() ?? "",
                r["contact"]?.GetValue<string?>(),
                Ts(r["timestamp"]),
                TimeSpan.FromSeconds(r["duration_seconds"]?.GetValue<double>() ?? 0));
        }
    }

    public async IAsyncEnumerable<MobileApp> AppsAsync(string backupPath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var resp = await sc.InvokeAsync("apps", new JsonObject { ["path"] = backupPath }, ct).ConfigureAwait(false);
        foreach (var r in ((resp as JsonObject)?["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new MobileApp(
                r["source"]?.GetValue<string>() ?? "mobile.apps",
                r["user"]?.GetValue<string?>(),
                r["package"]?.GetValue<string>() ?? "",
                r["display_name"]?.GetValue<string>() ?? "",
                r["version"]?.GetValue<string?>(),
                r["installed"]?.GetValue<string?>() is { } i ? Ts(i) : null,
                r["last_used"]?.GetValue<string?>() is { } u ? Ts(u) : null);
        }
    }
}
