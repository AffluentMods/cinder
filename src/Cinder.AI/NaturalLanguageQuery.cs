using System.Text.Json;

namespace Cinder.AI;

/// <summary>
/// Translates natural-language questions into Cinder's structured query language. The LLM
/// emits a JSON object matching <see cref="StructuredQuery"/>; Cinder validates it before
/// executing — the model never gets to run arbitrary SQL.
/// </summary>
public sealed class NaturalLanguageQuery
{
    private readonly IAiProvider _ai;
    private const string Schema = """
        {
          "kind": "timeline" | "search" | "browser_history" | "user_activity",
          "user": "<username or null>",
          "from_utc": "<ISO 8601 or null>",
          "to_utc":   "<ISO 8601 or null>",
          "sources": ["evtx", "browser.history", ...] | null,
          "text_contains": "<substring or null>",
          "limit": <int 1..1000>
        }
        """;

    public NaturalLanguageQuery(IAiProvider ai)
    {
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
    }

    public async Task<StructuredQuery?> TranslateAsync(string question, CancellationToken ct = default)
    {
        if (_ai is DisabledProvider)
        {
            return null;
        }
        var prompt = new AiPrompt(
            SystemMessage:
                "You translate forensic-analyst questions into a strict JSON query. Return ONLY a single JSON object, no prose. " +
                "Schema:\n" + Schema,
            Messages: [new AiMessage("user", question)],
            Options: new AiPromptOptions(MaxTokens: 400, Temperature: 0.0));

        var raw = await _ai.CompleteAsync(prompt, ct).ConfigureAwait(false);
        var json = ExtractJson(raw);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<StructuredQuery>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractJson(string raw)
    {
        var open = raw.IndexOf('{');
        var close = raw.LastIndexOf('}');
        return open >= 0 && close > open ? raw[open..(close + 1)] : "";
    }
}

public sealed record StructuredQuery(
    string Kind,
    string? User,
    string? FromUtc,
    string? ToUtc,
    IReadOnlyList<string>? Sources,
    string? TextContains,
    int? Limit);
