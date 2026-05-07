using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cinder.Cloud;

/// <summary>
/// Google Drive connector via the public REST v3 API + OAuth2 authorization-code with PKCE.
/// Requires a registered OAuth client_id (`LIMITATIONS.md → cloud-oauth-clients`); the client
/// is loaded from <see cref="ClientId"/> at runtime so distributors can override.
/// </summary>
public sealed class GoogleDriveConnector : ICloudConnector
{
    public string ProviderId => "google-drive";
    public string DisplayName => "Google Drive";
    public string ClientId { get; init; } = "";   // set from settings; never bundled in repo
    public string Scope { get; } = "https://www.googleapis.com/auth/drive.metadata.readonly https://www.googleapis.com/auth/drive.activity.readonly";

    private readonly HttpClient _http;
    private string? _pendingVerifier;

    public GoogleDriveConnector(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    public Task<Uri> BeginAuthAsync(string redirectLoopbackUri, CancellationToken ct)
    {
        var (verifier, challenge) = OAuthPkceHelper.GeneratePkcePair();
        _pendingVerifier = verifier;
        var url = OAuthPkceHelper.BuildAuthUrl("https://accounts.google.com/o/oauth2/v2/auth", new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectLoopbackUri,
            ["scope"] = Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["access_type"] = "offline",
            ["prompt"] = "consent",
        });
        return Task.FromResult(new Uri(url));
    }

    public async Task<CloudAuthorization> CompleteAuthAsync(string authorizationCode, string codeVerifier, string redirectLoopbackUri, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["code"] = authorizationCode,
            ["code_verifier"] = codeVerifier ?? _pendingVerifier ?? "",
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectLoopbackUri,
        });
        using var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var token = await resp.Content.ReadFromJsonAsync<OAuthPkceHelper.TokenResponse>(cancellationToken: ct).ConfigureAwait(false);
        if (token is null)
        {
            throw new InvalidOperationException("Empty token response from Google.");
        }
        return new CloudAuthorization(ProviderId, "primary", token.AccessToken, token.RefreshToken,
            token.ExpiresIn is { } e ? DateTimeOffset.UtcNow.AddSeconds(e) : null,
            token.Scope ?? Scope);
    }

    public async IAsyncEnumerable<CloudFile> ListFilesAsync(CloudAuthorization auth, [EnumeratorCancellation] CancellationToken ct)
    {
        var pageToken = "";
        do
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.googleapis.com/drive/v3/files?fields=files(id,name,size,createdTime,modifiedTime,sha256Checksum),nextPageToken&pageSize=1000{(string.IsNullOrEmpty(pageToken) ? "" : "&pageToken=" + pageToken)}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
            if (json is null) yield break;
            foreach (var f in (json["files"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                yield return new CloudFile(
                    Provider: ProviderId,
                    AccountId: auth.AccountId,
                    Path: f["name"]?.GetValue<string>() ?? "",
                    Size: long.TryParse(f["size"]?.GetValue<string?>(), out var s) ? s : 0,
                    Created: DateTimeOffset.TryParse(f["createdTime"]?.GetValue<string?>(), out var c) ? c : null,
                    Modified: DateTimeOffset.TryParse(f["modifiedTime"]?.GetValue<string?>(), out var m) ? m : null,
                    Sha256: f["sha256Checksum"]?.GetValue<string?>(),
                    RemoteId: f["id"]?.GetValue<string?>());
            }
            pageToken = json["nextPageToken"]?.GetValue<string?>() ?? "";
        }
        while (!string.IsNullOrEmpty(pageToken));
    }

    public async IAsyncEnumerable<CloudActivity> ListActivityAsync(CloudAuthorization auth, DateTimeOffset since, [EnumeratorCancellation] CancellationToken ct)
    {
        // Google Drive Activity API v2 — paginated; one round-trip pull.
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://driveactivity.googleapis.com/v2/activity:query")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                pageSize = 500,
                filter = $"time >= \"{since:O}\""
            }), System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
        foreach (var a in (json?["activities"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var t = a["timestamp"]?.GetValue<string?>() ?? a["timeRange"]?["startTime"]?.GetValue<string?>();
            yield return new CloudActivity(
                Provider: ProviderId, AccountId: auth.AccountId,
                Action: (a["primaryActionDetail"] as JsonObject)?.FirstOrDefault().Key ?? "?",
                Target: a["targets"]?[0]?["driveItem"]?["title"]?.GetValue<string?>() ?? "",
                Timestamp: DateTimeOffset.TryParse(t, out var dto) ? dto : DateTimeOffset.MinValue,
                IpAddress: null, UserAgent: null);
        }
    }
}
