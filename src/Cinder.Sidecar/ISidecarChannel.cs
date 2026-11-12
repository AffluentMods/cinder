using System.Text.Json.Nodes;

namespace Cinder.Sidecar;

/// <summary>
/// The one operation the sidecar adapters need: send a request, get its result. Implemented by
/// <see cref="SidecarClient"/> (a real process). It exists as an interface so that the adapters'
/// request building and response mapping can be tested against canned JSON without starting
/// Python.
/// </summary>
public interface ISidecarChannel : IAsyncDisposable
{
    /// <summary>
    /// Invokes <paramref name="method"/> and returns its <c>result</c>. Throws
    /// <see cref="SidecarException"/> when the sidecar reports an error, exits, or stays silent
    /// for longer than the inactivity timeout.
    /// </summary>
    /// <param name="method">JSON-RPC method name.</param>
    /// <param name="params">Request parameters.</param>
    /// <param name="progress">Receives the <c>params</c> of each <c>$/progress</c> notification
    /// the sidecar sends for this call; null to ignore them.</param>
    /// <param name="timeout">Inactivity timeout; the channel's default when null.</param>
    /// <param name="ct">Cancels this call only.</param>
    Task<JsonNode?> InvokeAsync(string method, JsonNode? @params, IProgress<JsonObject>? progress,
        TimeSpan? timeout = null, CancellationToken ct = default);
}
