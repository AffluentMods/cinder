using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Web;

namespace Cinder.Cloud;

/// <summary>
/// PKCE (RFC 7636) + state helper for the loopback OAuth flow RFC 8252 describes for native
/// apps: generate a verifier/challenge pair and a state nonce, open the system browser, listen
/// on a loopback HTTP server for the redirect, and refuse anything that does not carry the
/// state this flow issued. No client secret is involved.
///
/// <para>Each provider needs a user-registered OAuth client_id with a localhost redirect URI.
/// Cinder ships no baked-in client_ids; see <c>docs/cloud-setup.md</c>.</para>
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

    /// <summary>
    /// A fresh CSRF nonce for one authorization attempt. Sent as <c>state</c> and required back
    /// on the redirect — a callback that a different page, tab or process fires at the loopback
    /// listener will not carry it and is rejected.
    /// </summary>
    public static string GenerateState()
    {
        Span<byte> entropy = stackalloc byte[32];
        RandomNumberGenerator.Fill(entropy);
        return Base64UrlEncode(entropy);
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

    /// <summary>
    /// True for the only prefixes the redirect listener may bind: <c>http://127.0.0.1:port/</c>,
    /// <c>http://[::1]:port/</c>, or <c>http://localhost:port/</c>. Anything else would expose
    /// the authorization code to the network.
    /// </summary>
    public static bool IsLoopbackPrefix(string prefix)
    {
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }
        return uri.IsLoopback;
    }

    /// <summary>
    /// Waits for exactly one redirect on <paramref name="loopbackUri"/> and returns its
    /// authorization code.
    /// </summary>
    /// <exception cref="ArgumentException">The prefix is not a loopback address.</exception>
    /// <exception cref="OAuthRedirectException">
    /// The provider returned an <c>error</c>, the callback carried no code, or its <c>state</c>
    /// did not match <paramref name="expectedState"/>.
    /// </exception>
    public static async Task<string> AwaitRedirectCodeAsync(string loopbackUri, string expectedState, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loopbackUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedState);
        if (!IsLoopbackPrefix(loopbackUri))
        {
            throw new ArgumentException("The OAuth redirect listener may only bind a loopback address.", nameof(loopbackUri));
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add(loopbackUri.EndsWith('/') ? loopbackUri : loopbackUri + "/");
        listener.Start();
        try
        {
            using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
            var ctx = await listener.GetContextAsync().ConfigureAwait(false);
            var query = HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");

            var error = query["error"];
            var code = query["code"];
            var state = query["state"];

            string page;
            OAuthRedirectException? failure = null;
            if (!string.IsNullOrEmpty(error))
            {
                failure = new OAuthRedirectException($"The provider refused authorization: {error} {query["error_description"]}".Trim());
                page = "Authorization was refused. You can close this tab.";
            }
            else if (string.IsNullOrEmpty(state) || !FixedTimeEquals(state, expectedState))
            {
                failure = new OAuthRedirectException("The redirect did not carry the state this sign-in issued; it was ignored.");
                page = "This callback did not match a sign-in Cinder started. Nothing was accepted.";
            }
            else if (string.IsNullOrEmpty(code))
            {
                failure = new OAuthRedirectException("The redirect carried no authorization code.");
                page = "No authorization code was received. You can close this tab.";
            }
            else
            {
                page = "Authorization complete. You can close this tab.";
            }

            ctx.Response.StatusCode = failure is null ? 200 : 400;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var body = Encoding.UTF8.GetBytes(
                "<html><body style=\"font:14px system-ui;color:#FF7A1A;background:#0E0F12;text-align:center;padding-top:80px\"><h1>Cinder</h1><p>"
                + WebUtility.HtmlEncode(page) + "</p></body></html>");
            ctx.Response.ContentLength64 = body.Length;
            // willBlock: the listener is stopped as soon as this method returns, and http.sys
            // completes sends asynchronously — without blocking here the browser can get a
            // reset before the page arrives.
            ctx.Response.Close(body, willBlock: true);

            if (failure is not null)
            {
                throw failure;
            }
            return code!;
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
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

/// <summary>A redirect reached the loopback listener but could not be accepted.</summary>
public sealed class OAuthRedirectException(string message) : Exception(message);
