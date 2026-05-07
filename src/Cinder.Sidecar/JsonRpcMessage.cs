using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cinder.Sidecar;

/// <summary>
/// JSON-RPC 2.0 over newline-delimited JSON on stdio. Cinder sidecars must read one JSON object
/// per line on stdin and write one per line on stdout.
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
