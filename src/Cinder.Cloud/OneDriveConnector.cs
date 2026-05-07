using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Cinder.Cloud;

/// <summary>
/// Microsoft OneDrive / SharePoint connector via the Microsoft Graph API + delegated OAuth.
/// Same registration constraint as Google: an Azure App Registration with `http://127.0.0.1:&lt;port&gt;`
/// listed as a public-client redirect URI is required.
/// </summary>
public sealed class OneDriveConnector : ICloudConnector
{
    public string ProviderId => "onedrive";
    public string DisplayName => "Microsoft OneDrive";
    public string ClientId { get; init; } = "";
    public string Tenant { get; init; } = "common";
    public string Scope { get; } = "Files.Read.All Sites.Read.All offline_access";

    private readonly HttpClient _http;

    public OneDriveConnector(HttpClient http) => _http = http;

    public Task<Uri> BeginAuthAsync(string redirectLoopbackUri, CancellationToken ct)
    {
        var (verifier, challenge) = OAuthPkceHelper.GeneratePkcePair();
        _ = verifier; // surface via CompleteAuthAsync's codeVerifier parameter
        var url = OAuthPkceHelper.BuildAuthUrl($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize", new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectLoopbackUri,
            ["scope"] = Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });
        return Task.FromResult(new Uri(url));
    }

    public async Task<CloudAuthorization> CompleteAuthAsync(string authorizationCode, string codeVerifier, string redirectLoopbackUri, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId, ["code"] = authorizationCode, ["code_verifier"] = codeVerifier,
            ["grant_type"] = "authorization_code", ["redirect_uri"] = redirectLoopbackUri,
            ["scope"] = Scope,
        });
        using var resp = await _http.PostAsync($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var token = await resp.Content.ReadFromJsonAsync<OAuthPkceHelper.TokenResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty token response.");
        return new CloudAuthorization(ProviderId, "primary", token.AccessToken, token.RefreshToken,
            token.ExpiresIn is { } e ? DateTimeOffset.UtcNow.AddSeconds(e) : null, token.Scope ?? Scope);
    }

    public async IAsyncEnumerable<CloudFile> ListFilesAsync(CloudAuthorization auth, [EnumeratorCancellation] CancellationToken ct)
    {
        var url = "https://graph.microsoft.com/v1.0/me/drive/root/children?$top=200";
        while (!string.IsNullOrEmpty(url))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
            foreach (var f in (json?["value"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                yield return new CloudFile(
                    Provider: ProviderId, AccountId: auth.AccountId,
                    Path: f["name"]?.GetValue<string>() ?? "",
                    Size: f["size"]?.GetValue<long>() ?? 0,
                    Created: DateTimeOffset.TryParse(f["createdDateTime"]?.GetValue<string?>(), out var c) ? c : null,
                    Modified: DateTimeOffset.TryParse(f["lastModifiedDateTime"]?.GetValue<string?>(), out var m) ? m : null,
                    Sha256: (f["file"] as JsonObject)?["hashes"]?["sha256Hash"]?.GetValue<string?>(),
                    RemoteId: f["id"]?.GetValue<string?>());
            }
            url = json?["@odata.nextLink"]?.GetValue<string?>() ?? "";
        }
    }

    public async IAsyncEnumerable<CloudActivity> ListActivityAsync(CloudAuthorization auth, DateTimeOffset since, [EnumeratorCancellation] CancellationToken ct)
    {
        // Graph delta queries — TODO 10.1: complete the differential walker.
        await Task.Yield();
        _ = auth; _ = since; _ = ct;
        yield break;
    }
}
