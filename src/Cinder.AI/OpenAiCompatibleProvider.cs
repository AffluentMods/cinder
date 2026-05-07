using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cinder.AI;

/// <summary>
/// Generic OpenAI-compatible provider — works with vLLM, TGI, LocalAI, KoboldCpp, and any future
/// LLM gateway speaking the standard <c>/v1/chat/completions</c> shape. Streaming uses SSE.
///
/// Per <c>docs/plan.md §6</c>: Astryx, when it ships, will plug in here too — no Cinder code
/// changes required.
/// </summary>
public class OpenAiCompatibleProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly Func<string?> _apiKeyAccessor;

    public string Id { get; }
    public string DisplayName { get; }
    public AiProviderCapabilities Capabilities { get; }
    public string Model { get; }

    public OpenAiCompatibleProvider(HttpClient http, string id, string displayName, Uri endpoint, string model,
        Func<string?> apiKeyAccessor, AiProviderCapabilities? capabilities = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _http.BaseAddress = endpoint;
        Id = id;
        DisplayName = displayName;
        Model = model;
        _apiKeyAccessor = apiKeyAccessor ?? (() => null);
        Capabilities = capabilities ?? new AiProviderCapabilities(
            SupportsStreaming: true, SupportsToolCalling: false, SupportsVision: false,
            MaxContextTokens: 32_000, LocalOnly: endpoint.Host is "localhost" or "127.0.0.1");
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "models");
            ApplyAuth(req);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> CompleteAsync(AiPrompt prompt, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        ApplyAuth(req);
        req.Content = JsonContent.Create(BuildBody(prompt, stream: false));
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct).ConfigureAwait(false);
        return body?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
    }

    public async IAsyncEnumerable<string> StreamCompletionAsync(AiPrompt prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        ApplyAuth(req);
        req.Content = JsonContent.Create(BuildBody(prompt, stream: true));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }
            var payload = line[6..];
            if (payload == "[DONE]")
            {
                yield break;
            }
            var chunk = JsonSerializer.Deserialize<ChatChunk>(payload);
            var delta = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        var key = _apiKeyAccessor();
        if (!string.IsNullOrEmpty(key))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    private object BuildBody(AiPrompt prompt, bool stream)
    {
        var msgs = new List<object>(prompt.Messages.Count + 1);
        if (!string.IsNullOrEmpty(prompt.SystemMessage))
        {
            msgs.Add(new { role = "system", content = prompt.SystemMessage });
        }
        foreach (var m in prompt.Messages)
        {
            msgs.Add(new { role = m.Role, content = m.Content });
        }
        return new
        {
            model = Model,
            messages = msgs,
            temperature = prompt.Options.Temperature,
            max_tokens = prompt.Options.MaxTokens,
            stream,
        };
    }

    private sealed record ChatResponse([property: JsonPropertyName("choices")] List<Choice>? Choices);
    private sealed record Choice([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);
    private sealed record ChatChunk([property: JsonPropertyName("choices")] List<ChunkChoice>? Choices);
    private sealed record ChunkChoice([property: JsonPropertyName("delta")] ChunkDelta? Delta);
    private sealed record ChunkDelta([property: JsonPropertyName("content")] string? Content);
}
