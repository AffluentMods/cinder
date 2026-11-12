using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Cinder.Cloud;

public sealed class DropboxConnector : ICloudConnector
{
    public string ProviderId => "dropbox";
    public string DisplayName => "Dropbox";
    public string ClientId { get; init; } = "";
    public string Scope { get; } = "files.metadata.read";

    /// <summary>Value of <see cref="CloudFile.ProviderHashAlgorithm"/> for the block-wise Dropbox digest.</summary>
    public const string ContentHashAlgorithm = "dropbox-content-hash";

    private readonly HttpClient _http;
    public DropboxConnector(HttpClient http) => _http = http;

    private string? _pendingVerifier;

    public string? PendingState { get; private set; }

    public Task<Uri> BeginAuthAsync(string redirectLoopbackUri, CancellationToken ct)
    {
        // The verifier must survive until CompleteAuthAsync — it was previously discarded here,
        // which made the exchange impossible to complete.
        var (verifier, challenge) = OAuthPkceHelper.GeneratePkcePair();
        _pendingVerifier = verifier;
        PendingState = OAuthPkceHelper.GenerateState();
        var url = OAuthPkceHelper.BuildAuthUrl("https://www.dropbox.com/oauth2/authorize", new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectLoopbackUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["token_access_type"] = "offline",
            ["state"] = PendingState,
        });
        return Task.FromResult(new Uri(url));
    }

    public async Task<CloudAuthorization> CompleteAuthAsync(string authorizationCode, string codeVerifier, string redirectLoopbackUri, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = authorizationCode,
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code_verifier"] = string.IsNullOrEmpty(codeVerifier) ? _pendingVerifier ?? "" : codeVerifier,
            ["redirect_uri"] = redirectLoopbackUri,
        });
        using var resp = await _http.PostAsync("https://api.dropboxapi.com/oauth2/token", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var token = await resp.Content.ReadFromJsonAsync<OAuthPkceHelper.TokenResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty token response.");
        return new CloudAuthorization(ProviderId, "primary", token.AccessToken, token.RefreshToken,
            token.ExpiresIn is { } e ? DateTimeOffset.UtcNow.AddSeconds(e) : null, token.Scope ?? Scope);
    }

    public async IAsyncEnumerable<CloudFile> ListFilesAsync(CloudAuthorization auth, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new { path = "", recursive = true, limit = 1000 };
        var url = "https://api.dropboxapi.com/2/files/list_folder";
        var nextCursor = "";
        do
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(string.IsNullOrEmpty(nextCursor) ? (object)body : new { cursor = nextCursor })
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
            foreach (var e in (json?["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                yield return new CloudFile(
                    Provider: ProviderId, AccountId: auth.AccountId,
                    Path: e["path_display"]?.GetValue<string>() ?? "",
                    Size: e["size"]?.GetValue<long>() ?? 0,
                    Created: null,
                    Modified: DateTimeOffset.TryParse(e["client_modified"]?.GetValue<string?>(), out var m) ? m : null,
                    // Dropbox's content_hash is SHA-256 over the concatenated SHA-256s of each
                    // 4 MiB block: 64 hex characters, but not the SHA-256 of the file, for any
                    // file of any size. Labelling it Sha256 made every hash-set lookup and every
                    // comparison with a locally computed hash miss.
                    Sha256: null,
                    RemoteId: e["id"]?.GetValue<string?>(),
                    ProviderHash: e["content_hash"]?.GetValue<string?>(),
                    ProviderHashAlgorithm: e["content_hash"] is null ? null : ContentHashAlgorithm);
            }
            url = "https://api.dropboxapi.com/2/files/list_folder/continue";
            nextCursor = (json?["has_more"]?.GetValue<bool>() ?? false) ? json!["cursor"]!.GetValue<string>() : "";
        }
        while (!string.IsNullOrEmpty(nextCursor));
    }

    public async IAsyncEnumerable<CloudActivity> ListActivityAsync(CloudAuthorization auth, DateTimeOffset since, [EnumeratorCancellation] CancellationToken ct)
    {
        // Dropbox Business Audit Log API — only available on Business tiers.
        await Task.Yield();
        _ = auth; _ = since; _ = ct;
        yield break;
    }
}
