using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Web;

namespace Cinder.Cloud;

/// <summary>
/// PKCE (RFC 7636) helper. Cinder cloud connectors generate one verifier per auth attempt,
/// open the system browser, and listen on a loopback HTTP server for the redirect — no client
/// secret required, suitable for a desktop app.
///
/// **TODO**: each provider needs a registered OAuth client_id with `http://127.0.0.1:&lt;port&gt;`
/// as an allowed redirect URI. Cinder cannot register these automatically; see
/// <c>LIMITATIONS.md → cloud-oauth-clients</c>.
/// </summary>
public static class OAuthPkceHelper
{
    public static (string Verifier, string Challenge) GeneratePkcePair()
    {
        Span<byte> entropy = stackalloc byte[32];
        RandomNumberGenerator.Fill(entropy);
        var verifier = Base64UrlEncode(entropy);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64UrlEncode(hash);
        return (verifier, challenge);
    }

    public static string BuildAuthUrl(string authorizeEndpoint, IDictionary<string, string> queryParams)
    {
        var parts = HttpUtility.ParseQueryString(string.Empty);
        foreach (var kv in queryParams)
        {
            parts[kv.Key] = kv.Value;
        }
        return authorizeEndpoint + "?" + parts;
    }

    public static async Task<string> AwaitRedirectCodeAsync(string loopbackUri, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(loopbackUri.EndsWith("/") ? loopbackUri : loopbackUri + "/");
        listener.Start();
        try
        {
            using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
            var ctx = await listener.GetContextAsync().ConfigureAwait(false);
            var query = HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
            var code = query["code"];
            using var w = new StreamWriter(ctx.Response.OutputStream, Encoding.UTF8, leaveOpen: true);
            w.Write("<html><body style=\"font:14px system-ui;color:#FF7A1A;background:#0E0F12;text-align:center;padding-top:80px\"><h1>Cinder</h1><p>Authorization complete. You can close this tab.</p></body></html>");
            w.Flush();
            ctx.Response.Close();
            return code ?? "";
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        var s = Convert.ToBase64String(bytes);
        return s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope);
}
