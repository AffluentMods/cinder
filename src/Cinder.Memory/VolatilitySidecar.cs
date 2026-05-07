using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Memory;

/// <summary>
/// Wraps <c>parsers/memory/vol_worker.py</c> which embeds Volatility 3. Each plugin maps to
/// a method here; results are emitted as JSON. The C# layer is plugin-agnostic and can run
/// arbitrary vol3 plugins via <see cref="RunPluginAsync"/>.
/// </summary>
public sealed class VolatilitySidecar
{
    private readonly Func<ProcessStartInfo> _factory;
    private readonly ILogger _logger;

    public VolatilitySidecar(Func<ProcessStartInfo> factory, ILogger? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? NullLogger.Instance;
    }

    public static ProcessStartInfo DefaultSidecar(string parsersDir) => new()
    {
        FileName = OperatingSystem.IsWindows() ? "python.exe" : "python3",
        ArgumentList = { "-m", "memory.vol_worker" },
        WorkingDirectory = parsersDir,
    };

    private static DateTimeOffset? Ts(JsonNode? n) =>
        DateTimeOffset.TryParse(n?.GetValue<string?>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto) ? dto : null;

    public async IAsyncEnumerable<MemoryProcess> ProcessTreeAsync(string memImagePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var resp = await sc.InvokeAsync("pstree", new JsonObject { ["image"] = memImagePath }, ct).ConfigureAwait(false);
        foreach (var r in ((resp as JsonObject)?["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var anomalies = (r["anomalies"] as JsonArray)?.Select(a => a?.GetValue<string>() ?? "").ToList() ?? [];
            yield return new MemoryProcess(
                Pid: r["pid"]?.GetValue<int>() ?? 0,
                ParentPid: r["ppid"]?.GetValue<int>() ?? 0,
                ImageName: r["image"]?.GetValue<string>() ?? "",
                CommandLine: r["cmdline"]?.GetValue<string?>(),
                CreatedAt: Ts(r["created_at"]),
                ExitedAt: Ts(r["exited_at"]),
                Threads: r["threads"]?.GetValue<int>() ?? 0,
                Handles: r["handles"]?.GetValue<int>() ?? 0,
                SessionId: r["session"]?.GetValue<string?>(),
                IntegrityLevel: r["integrity"]?.GetValue<string?>(),
                Suspicious: anomalies.Count > 0,
                Anomalies: anomalies);
        }
    }

    public async IAsyncEnumerable<MemoryConnection> NetScanAsync(string memImagePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var resp = await sc.InvokeAsync("netscan", new JsonObject { ["image"] = memImagePath }, ct).ConfigureAwait(false);
        foreach (var r in ((resp as JsonObject)?["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new MemoryConnection(
                Pid: r["pid"]?.GetValue<int>() ?? 0,
                Protocol: r["proto"]?.GetValue<string>() ?? "",
                LocalAddress: r["local_addr"]?.GetValue<string>() ?? "",
                LocalPort: r["local_port"]?.GetValue<int>() ?? 0,
                RemoteAddress: r["remote_addr"]?.GetValue<string>() ?? "",
                RemotePort: r["remote_port"]?.GetValue<int>() ?? 0,
                State: r["state"]?.GetValue<string>() ?? "",
                CreatedAt: Ts(r["created_at"]));
        }
    }

    public async IAsyncEnumerable<LoadedModule> ListModulesAsync(string memImagePath, int? pid = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var args = new JsonObject { ["image"] = memImagePath };
        if (pid.HasValue) args["pid"] = pid.Value;
        var resp = await sc.InvokeAsync("dlllist", args, ct).ConfigureAwait(false);
        foreach (var r in ((resp as JsonObject)?["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new LoadedModule(
                Pid: r["pid"]?.GetValue<int>() ?? 0,
                ModuleName: r["name"]?.GetValue<string>() ?? "",
                Path: r["path"]?.GetValue<string>() ?? "",
                BaseAddress: r["base"]?.GetValue<string?>(),
                Size: r["size"]?.GetValue<long>() ?? 0,
                IsSigned: r["signed"]?.GetValue<bool>() ?? false);
        }
    }

    public async IAsyncEnumerable<InjectionFinding> MalfindAsync(string memImagePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var resp = await sc.InvokeAsync("malfind", new JsonObject { ["image"] = memImagePath }, ct).ConfigureAwait(false);
        foreach (var r in ((resp as JsonObject)?["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new InjectionFinding(
                Pid: r["pid"]?.GetValue<int>() ?? 0,
                ImageName: r["image_name"]?.GetValue<string>() ?? "",
                Type: r["type"]?.GetValue<string>() ?? "malfind",
                Address: r["address"]?.GetValue<string?>(),
                Length: r["length"]?.GetValue<long>() ?? 0,
                Notes: r["notes"]?.GetValue<string?>());
        }
    }

    public async IAsyncEnumerable<CredentialDump> HashDumpAsync(string memImagePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var resp = await sc.InvokeAsync("hashdump", new JsonObject { ["image"] = memImagePath }, ct).ConfigureAwait(false);
        foreach (var r in ((resp as JsonObject)?["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            yield return new CredentialDump(
                Source: r["plugin"]?.GetValue<string>() ?? "hashdump",
                Account: r["account"]?.GetValue<string>() ?? "",
                Domain: r["domain"]?.GetValue<string?>(),
                Hash: r["hash"]?.GetValue<string?>(),
                LastChange: Ts(r["last_change"]));
        }
    }

    /// <summary>Run an arbitrary Volatility 3 plugin and return the raw JSON output.</summary>
    public async Task<JsonNode?> RunPluginAsync(string memImagePath, string pluginId, IReadOnlyDictionary<string, object>? options = null, CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_factory(), _logger);
        var args = new JsonObject { ["image"] = memImagePath, ["plugin"] = pluginId };
        if (options is { Count: > 0 })
        {
            var optsObj = new JsonObject();
            foreach (var kv in options)
            {
                optsObj[kv.Key] = JsonValue.Create(kv.Value);
            }
            args["options"] = optsObj;
        }
        return await sc.InvokeAsync("run_plugin", args, ct).ConfigureAwait(false);
    }
}
