using System.Diagnostics;
using System.Text.Json.Nodes;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Cinder.Core.Diagnostics;

namespace Cinder.Plugins;

/// <summary>
/// Embedded Python scripting host. Spawns a long-running Python sidecar and evaluates scripts
/// in it.
///
/// <para><b>There is no sandbox.</b> A script runs as ordinary Python with the examiner's full
/// rights: it can read and write any file, open network connections and start processes. An
/// earlier comment here promised confinement to the case directory; nothing ever enforced
/// that, and restricting <c>exec</c> is not a security boundary. Treat a script exactly like a
/// program: run only ones you trust.</para>
/// </summary>
public sealed class PythonScriptingHost : IAsyncDisposable
{
    private readonly SidecarClient _client;

    public PythonScriptingHost(string parsersDir, ILogger? logger = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExecutableResolver.ResolveRequired(
            OperatingSystem.IsWindows() ? "python.exe" : "python3",
            "Install Python 3.12+ and make sure it is on PATH."),
            ArgumentList = { "-m", "scripting.script_host" },
            WorkingDirectory = parsersDir,
        };
        _client = new SidecarClient(psi, logger ?? NullLogger.Instance);
    }

    public async Task<JsonNode?> EvalAsync(string script, IDictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        var args = new JsonObject { ["script"] = script };
        if (parameters is { Count: > 0 })
        {
            var p = new JsonObject();
            foreach (var kv in parameters)
            {
                p[kv.Key] = JsonValue.Create(kv.Value);
            }
            args["params"] = p;
        }
        return await _client.InvokeAsync("eval", args, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
