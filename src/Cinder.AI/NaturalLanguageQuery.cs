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
        return Parse(json);
    }

    private static readonly JsonSerializerOptions QueryJson = new()
    {
        // The schema the model is given is snake_case (from_utc, text_contains). Case-insensitive
        // matching alone does not bridge that to FromUtc/TextContains: those members stayed null
        // and the date and text filters were silently dropped from every translated query.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] AllowedKinds = ["timeline", "search", "browser_history", "user_activity"];

    /// <summary>
    /// Parses and validates the model's JSON. Returns null — "could not translate" — rather than
    /// a query with a filter quietly missing: an unknown kind, an unparseable or inverted date
    /// range, or malformed JSON all fail closed. <c>limit</c> is clamped to the schema's 1..1000.
    /// </summary>
    public static StructuredQuery? Parse(string json)
    {
        StructuredQuery? query;
        try
        {
            query = JsonSerializer.Deserialize<StructuredQuery>(json, QueryJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (query?.Kind is null)
        {
            return null;
        }
        var kind = AllowedKinds.FirstOrDefault(k => k.Equals(query.Kind.Trim(), StringComparison.OrdinalIgnoreCase));
        if (kind is null)
        {
            return null;
        }

        if (!TryParseInstant(query.FromUtc, out var from) || !TryParseInstant(query.ToUtc, out var to))
        {
            return null;
        }
        if (from is not null && to is not null && from > to)
        {
            return null;
        }

        return query with
        {
            Kind = kind,
            User = string.IsNullOrWhiteSpace(query.User) ? null : query.User,
            FromUtc = from?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ToUtc = to?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            TextContains = string.IsNullOrEmpty(query.TextContains) ? null : query.TextContains,
            Limit = query.Limit is { } limit ? Math.Clamp(limit, 1, 1000) : null,
        };
    }

    private static bool TryParseInstant(string? text, out DateTimeOffset? instant)
    {
        instant = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }
        if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            instant = parsed;
            return true;
        }
        return false;
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
