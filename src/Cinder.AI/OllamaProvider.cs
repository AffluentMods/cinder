using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cinder.AI;

/// <summary>
/// Ollama provider — auto-detects <c>localhost:11434</c>. Uses the native /api/chat endpoint
/// which streams JSON-per-line (not SSE).
/// </summary>
public sealed class OllamaProvider : IAiProvider
{
    private readonly HttpClient _http;
    public string Id => "ollama";
    public string DisplayName { get; }
    public string Model { get; }
    public AiProviderCapabilities Capabilities { get; } = new(
        SupportsStreaming: true, SupportsToolCalling: true, SupportsVision: true,
        MaxContextTokens: 8_192, LocalOnly: true);

    public OllamaProvider(HttpClient http, string model = "llama3", Uri? endpoint = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _http.BaseAddress = endpoint ?? new Uri("http://localhost:11434");
        Model = model;
        DisplayName = $"Ollama · {model}";
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("api/tags", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> CompleteAsync(AiPrompt prompt, CancellationToken ct)
    {
        var body = BuildBody(prompt, stream: false);
        using var resp = await _http.PostAsJsonAsync("api/chat", body, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var parsed = await resp.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct).ConfigureAwait(false);
        return parsed?.Message?.Content ?? "";
    }

    public async IAsyncEnumerable<string> StreamCompletionAsync(AiPrompt prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildBody(prompt, stream: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = JsonContent.Create(body) };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }
            var chunk = JsonSerializer.Deserialize<ChatResponse>(line);
            var token = chunk?.Message?.Content;
            if (!string.IsNullOrEmpty(token))
            {
                yield return token;
            }
            if (chunk?.Done == true)
            {
                yield break;
            }
        }
    }

    private object BuildBody(AiPrompt prompt, bool stream)
    {
        var msgs = new List<object>();
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
            stream,
            options = new { temperature = prompt.Options.Temperature, num_predict = prompt.Options.MaxTokens },
        };
    }

    private sealed record ChatResponse(
        [property: JsonPropertyName("message")] ChatMessage? Message,
        [property: JsonPropertyName("done")] bool Done);
    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);
}
