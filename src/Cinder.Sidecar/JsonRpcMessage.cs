using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cinder.Sidecar;

/// <summary>
/// JSON-RPC 2.0 over newline-delimited JSON on stdio. Cinder sidecars must read one JSON object
/// per line on stdin and write one per line on stdout, both as UTF-8 without a BOM, and must
/// write nothing else to stdout. A sidecar answers a line it cannot parse with an error that
/// has no <c>id</c>, and may send <c>$/progress</c> notifications (no <c>id</c>) while a long
/// call is running; see <see cref="SidecarClient"/> for how both are handled.
/// </summary>
public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; } = "2.0";
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("method")] public required string Method { get; init; }
    [JsonPropertyName("params")] public JsonNode? Params { get; init; }
}

public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; } = "2.0";
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("result")] public JsonNode? Result { get; init; }
    [JsonPropertyName("error")] public JsonRpcError? Error { get; init; }
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")] public int Code { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("data")] public JsonNode? Data { get; init; }
}
