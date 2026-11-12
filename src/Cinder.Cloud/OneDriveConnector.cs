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

    private string? _pendingVerifier;

    public string? PendingState { get; private set; }

    public Task<Uri> BeginAuthAsync(string redirectLoopbackUri, CancellationToken ct)
    {
        var (verifier, challenge) = OAuthPkceHelper.GeneratePkcePair();
        _pendingVerifier = verifier;
        var url = OAuthPkceHelper.BuildAuthUrl($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize", new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectLoopbackUri,
            ["scope"] = Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = PendingState = OAuthPkceHelper.GenerateState(),
        });
        return Task.FromResult(new Uri(url));
    }

    public async Task<CloudAuthorization> CompleteAuthAsync(string authorizationCode, string codeVerifier, string redirectLoopbackUri, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["code"] = authorizationCode,
            ["code_verifier"] = string.IsNullOrEmpty(codeVerifier) ? _pendingVerifier ?? "" : codeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectLoopbackUri,
            ["scope"] = Scope,
        });
        using var resp = await _http.PostAsync($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var token = await resp.Content.ReadFromJsonAsync<OAuthPkceHelper.TokenResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty token response.");
        return new CloudAuthorization(ProviderId, "primary", token.AccessToken, token.RefreshToken,
            token.ExpiresIn is { } e ? DateTimeOffset.UtcNow.AddSeconds(e) : null, token.Scope ?? Scope);
    }

    /// <summary>Value of <see cref="CloudFile.ProviderHashAlgorithm"/> for OneDrive's QuickXorHash.</summary>
    public const string QuickXorHashAlgorithm = "onedrive-quickxorhash";

    /// <summary>
    /// Every file in the drive, at any depth.
    ///
    /// <para>Uses the drive <c>delta</c> feed rather than <c>root/children</c>: children lists one
    /// folder level, so everything inside a subfolder was missing from the collection with no
    /// indication. A first delta call (no token) enumerates the whole hierarchy; it is followed
    /// through <c>@odata.nextLink</c> until the page that carries <c>@odata.deltaLink</c>.
    /// Folders, the root item and tombstones for deleted items are skipped — this lists files.</para>
    /// </summary>
    public async IAsyncEnumerable<CloudFile> ListFilesAsync(CloudAuthorization auth, [EnumeratorCancellation] CancellationToken ct)
    {
        var url = "https://graph.microsoft.com/v1.0/me/drive/root/delta?$top=200";
        while (!string.IsNullOrEmpty(url))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
            foreach (var f in (json?["value"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if (f["folder"] is not null || f["root"] is not null || f["deleted"] is not null || f["file"] is null)
                {
                    continue;
                }
                var hashes = (f["file"] as JsonObject)?["hashes"] as JsonObject;
                var quickXor = hashes?["quickXorHash"]?.GetValue<string?>();
                yield return new CloudFile(
                    Provider: ProviderId, AccountId: auth.AccountId,
                    Path: FullPath(f),
                    Size: f["size"]?.GetValue<long>() ?? 0,
                    Created: DateTimeOffset.TryParse(f["createdDateTime"]?.GetValue<string?>(), out var c) ? c : null,
                    Modified: DateTimeOffset.TryParse(f["lastModifiedDateTime"]?.GetValue<string?>(), out var m) ? m : null,
                    Sha256: hashes?["sha256Hash"]?.GetValue<string?>(),
                    RemoteId: f["id"]?.GetValue<string?>(),
                    ProviderHash: quickXor,
                    ProviderHashAlgorithm: quickXor is null ? null : QuickXorHashAlgorithm);
            }

            // Only follow links back to Graph: the bearer token goes wherever this URL points.
            var next = json?["@odata.nextLink"]?.GetValue<string?>() ?? "";
            if (next.Length > 0 &&
                !(Uri.TryCreate(next, UriKind.Absolute, out var nextUri) &&
                  nextUri.Scheme == Uri.UriSchemeHttps &&
                  nextUri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"OneDrive returned a next-page link outside Microsoft Graph: {next}");
            }
            url = next;
        }
    }

    /// <summary>
    /// Path within the drive: <c>parentReference.path</c> is <c>/drive/root:/Folder/Sub</c>
    /// (percent-encoded); the part after <c>root:</c> plus the item name is what a user sees.
    /// </summary>
    private static string FullPath(JsonObject item)
    {
        var name = item["name"]?.GetValue<string>() ?? "";
        var parent = (item["parentReference"] as JsonObject)?["path"]?.GetValue<string?>() ?? "";
        var marker = parent.IndexOf("root:", StringComparison.Ordinal);
        var folder = marker >= 0 ? Uri.UnescapeDataString(parent[(marker + 5)..]) : "";
        return folder.TrimEnd('/') + "/" + name;
    }

    public async IAsyncEnumerable<CloudActivity> ListActivityAsync(CloudAuthorization auth, DateTimeOffset since, [EnumeratorCancellation] CancellationToken ct)
    {
        // Graph delta queries — TODO 10.1: complete the differential walker.
        await Task.Yield();
        _ = auth; _ = since; _ = ct;
        yield break;
    }
}
