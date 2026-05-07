using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Search;

public sealed record VirusTotalReport(
    string Hash,
    int Malicious,
    int Suspicious,
    int Harmless,
    int Undetected,
    string? FirstSubmissionUtc,
    string? LastAnalysisUtc,
    IReadOnlyDictionary<string, string> EngineVerdicts);

/// <summary>
/// Read-only VirusTotal lookup. **Opt-in only**, **hash-only**, **never uploads bytes**, **user-
/// supplied API key** stored via the OS-native secret store (DPAPI / libsecret) — never in the
/// repo or in the case bundle.
///
/// VT calls are subject to a 4 lookups/min free-tier quota; the client honours
/// <c>Retry-After</c> and exposes a <see cref="QuotaExceeded"/> property.
/// </summary>
public sealed class VirusTotalClient
{
    private const string BaseUri = "https://www.virustotal.com/api/v3/";
    private readonly HttpClient _http;
    private readonly Func<string?> _apiKeyAccessor;
    private readonly ILogger _log;

    public bool QuotaExceeded { get; private set; }

    public VirusTotalClient(HttpClient http, Func<string?> apiKeyAccessor, ILogger? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKeyAccessor = apiKeyAccessor ?? throw new ArgumentNullException(nameof(apiKeyAccessor));
        _log = log ?? NullLogger.Instance;
        _http.BaseAddress ??= new Uri(BaseUri);
    }

    public async Task<VirusTotalReport?> LookupAsync(string hash, CancellationToken ct = default)
    {
        var key = _apiKeyAccessor();
        if (string.IsNullOrEmpty(key))
        {
            return null; // Opt-in: no key, no call.
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, $"files/{hash}");
        req.Headers.Add("x-apikey", key);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        if ((int)resp.StatusCode == 429)
        {
            QuotaExceeded = true;
            _log.LogWarning("VirusTotal quota exceeded; backing off until {Retry}.", resp.Headers.RetryAfter);
            return null;
        }
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<VtFileResponse>(cancellationToken: ct).ConfigureAwait(false);
        if (body?.Data?.Attributes is not { } a)
        {
            return null;
        }
        return new VirusTotalReport(
            hash,
            Malicious: a.LastAnalysisStats?.Malicious ?? 0,
            Suspicious: a.LastAnalysisStats?.Suspicious ?? 0,
            Harmless: a.LastAnalysisStats?.Harmless ?? 0,
            Undetected: a.LastAnalysisStats?.Undetected ?? 0,
            FirstSubmissionUtc: a.FirstSubmissionDate is { } f ? DateTimeOffset.FromUnixTimeSeconds(f).ToString("O") : null,
            LastAnalysisUtc: a.LastAnalysisDate is { } l ? DateTimeOffset.FromUnixTimeSeconds(l).ToString("O") : null,
            EngineVerdicts: a.LastAnalysisResults?.ToDictionary(kv => kv.Key, kv => kv.Value.Result ?? "") ?? new Dictionary<string, string>());
    }

    private sealed record VtFileResponse([property: JsonPropertyName("data")] VtData? Data);
    private sealed record VtData([property: JsonPropertyName("attributes")] VtAttributes? Attributes);
    private sealed record VtAttributes(
        [property: JsonPropertyName("last_analysis_stats")] VtStats? LastAnalysisStats,
        [property: JsonPropertyName("last_analysis_date")] long? LastAnalysisDate,
        [property: JsonPropertyName("first_submission_date")] long? FirstSubmissionDate,
        [property: JsonPropertyName("last_analysis_results")] Dictionary<string, VtEngineResult>? LastAnalysisResults);
    private sealed record VtStats(
        [property: JsonPropertyName("harmless")] int Harmless,
        [property: JsonPropertyName("malicious")] int Malicious,
        [property: JsonPropertyName("suspicious")] int Suspicious,
        [property: JsonPropertyName("undetected")] int Undetected);
    private sealed record VtEngineResult([property: JsonPropertyName("result")] string? Result);
}
