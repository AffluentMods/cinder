using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cinder.Core.Diagnostics;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Memory;

/// <summary>
/// Wraps <c>parsers/memory/vol_worker.py</c> which embeds Volatility 3. Each plugin maps to
/// a method here; results are emitted as JSON. The C# layer is plugin-agnostic and can run
/// arbitrary vol3 plugins via <see cref="RunPluginAsync"/>.
/// <para>
/// A field Volatility could not read from the image arrives as null and stays null (see
/// <c>MemoryArtifacts.cs</c>); it is never turned into 0.
/// </para>
/// </summary>
public sealed class VolatilitySidecar
{
    private readonly Func<ISidecarChannel> _open;

    public VolatilitySidecar(Func<ProcessStartInfo> factory, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var log = logger ?? NullLogger.Instance;
        _open = () => new SidecarClient(factory(), log);
    }

    /// <summary>Uses an already-built channel factory (tests, or a host that pools sidecars).</summary>
    public VolatilitySidecar(Func<ISidecarChannel> channelFactory)
    {
        _open = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
    }

    public static ProcessStartInfo DefaultSidecar(string parsersDir) => new()
    {
        FileName = ExecutableResolver.ResolveRequired(
            OperatingSystem.IsWindows() ? "python.exe" : "python3",
            "Install Python 3.12+ and make sure it is on PATH."),
        ArgumentList = { "-m", "memory.vol_worker" },
        WorkingDirectory = parsersDir,
    };

    // Kernel timestamps are UTC whether or not the text Volatility produced carries an offset.
    private static DateTimeOffset? Ts(JsonNode? n) => SidecarJson.Instant(n, utcByDefinition: true);

    private static string Str(JsonNode? n) => SidecarJson.String(n) ?? "";

    private async Task<IEnumerable<JsonObject>> EntriesAsync(string method, JsonObject args, CancellationToken ct)
    {
        await using var sc = _open();
        var resp = await sc.InvokeAsync(method, args, null, null, ct).ConfigureAwait(false);
        return SidecarJson.Objects((resp as JsonObject)?["entries"]);
    }

    public async IAsyncEnumerable<MemoryProcess> ProcessTreeAsync(string memImagePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var r in await EntriesAsync("pstree", new JsonObject { ["image"] = memImagePath }, ct).ConfigureAwait(false))
        {
            var anomalies = SidecarJson.Strings(r["anomalies"]);
            yield return new MemoryProcess(
                Pid: SidecarJson.Int32(r["pid"]),
                ParentPid: SidecarJson.Int32(r["ppid"]),
                ImageName: Str(r["image"]),
                CommandLine: SidecarJson.String(r["cmdline"]),
                CreatedAt: Ts(r["created_at"]),
                ExitedAt: Ts(r["exited_at"]),
                Threads: SidecarJson.Int32(r["threads"]),
                Handles: SidecarJson.Int32(r["handles"]),
                SessionId: SidecarJson.String(r["session"]),
                IntegrityLevel: SidecarJson.String(r["integrity"]),
                Suspicious: anomalies.Count > 0,
                Anomalies: anomalies);
        }
    }

    public async IAsyncEnumerable<MemoryConnection> NetScanAsync(string memImagePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var r in await EntriesAsync("netscan", new JsonObject { ["image"] = memImagePath }, ct).ConfigureAwait(false))
        {
            yield return new MemoryConnection(
                Pid: SidecarJson.Int32(r["pid"]),
                Protocol: Str(r["proto"]),
                LocalAddress: Str(r["local_addr"]),
                LocalPort: SidecarJson.Int32(r["local_port"]),
                RemoteAddress: Str(r["remote_addr"]),
                RemotePort: SidecarJson.Int32(r["remote_port"]),
                State: Str(r["state"]),
                CreatedAt: Ts(r["created_at"]));
        }
    }

    public async IAsyncEnumerable<LoadedModule> ListModulesAsync(string memImagePath, int? pid = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var args = new JsonObject { ["image"] = memImagePath };
        if (pid.HasValue)
        {
            args["pid"] = pid.Value;
        }
        foreach (var r in await EntriesAsync("dlllist", args, ct).ConfigureAwait(false))
        {
            yield return new LoadedModule(
                Pid: SidecarJson.Int32(r["pid"]),
                ModuleName: Str(r["name"]),
                Path: Str(r["path"]),
                BaseAddress: SidecarJson.String(r["base"]),
                Size: SidecarJson.Int64(r["size"]),
                IsSigned: SidecarJson.Boolean(r["signed"]) ?? false);
        }
    }

    public async IAsyncEnumerable<InjectionFinding> MalfindAsync(string memImagePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var r in await EntriesAsync("malfind", new JsonObject { ["image"] = memImagePath }, ct).ConfigureAwait(false))
        {
            yield return new InjectionFinding(
                Pid: SidecarJson.Int32(r["pid"]),
                ImageName: Str(r["image_name"]),
                Type: SidecarJson.String(r["type"]) ?? "malfind",
                Address: SidecarJson.String(r["address"]),
                Length: SidecarJson.Int64(r["length"]),
                Notes: SidecarJson.String(r["notes"]));
        }
    }

    public async IAsyncEnumerable<CredentialDump> HashDumpAsync(string memImagePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var r in await EntriesAsync("hashdump", new JsonObject { ["image"] = memImagePath }, ct).ConfigureAwait(false))
        {
            yield return new CredentialDump(
                Source: SidecarJson.String(r["plugin"]) ?? "hashdump",
                Account: Str(r["account"]),
                Domain: SidecarJson.String(r["domain"]),
                Hash: SidecarJson.String(r["hash"]),
                LastChange: Ts(r["last_change"]),
                Rid: SidecarJson.Int32(r["rid"]),
                LmHash: SidecarJson.String(r["lm"]),
                NtHash: SidecarJson.String(r["nt"]));
        }
    }

    /// <summary>Run an arbitrary Volatility 3 plugin and return the raw JSON output.</summary>
    public async Task<JsonNode?> RunPluginAsync(string memImagePath, string pluginId, IReadOnlyDictionary<string, object>? options = null, CancellationToken ct = default)
    {
        await using var sc = _open();
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
        return await sc.InvokeAsync("run_plugin", args, null, null, ct).ConfigureAwait(false);
    }
}
