using System.Diagnostics;
using System.Text.Json.Nodes;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Plugins;

/// <summary>
/// Embedded Python scripting host. Spawns a long-running Python sidecar that exposes the case
/// API: <c>cinder.case</c>, <c>cinder.timeline</c>, <c>cinder.artifacts</c>, <c>cinder.search</c>.
/// Scripts run as untrusted code (no file IO outside the case dir, no network) — enforced
/// loosely in the harness; trustworthy scripts can be marked "trusted" in settings.
/// </summary>
public sealed class PythonScriptingHost : IAsyncDisposable
{
    private readonly SidecarClient _client;

    public PythonScriptingHost(string parsersDir, ILogger? logger = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "python.exe" : "python3",
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
