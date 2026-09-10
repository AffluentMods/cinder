using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace Cinder.Core.Custody;

/// <summary>A trusted-timestamp token and what it says.</summary>
public sealed record TimestampResult(byte[] Token, DateTimeOffset Time, string? TsaSubject, string? PolicyOid);

/// <summary>
/// Outcome of checking a token against the data it should cover. <see cref="SignatureValid"/>
/// is the cryptographic fact: the token's message imprint matches the data and its CMS
/// signature verifies under the certificate it carries. <see cref="ChainTrusted"/> is whether
/// that certificate chains to a root this machine trusts — informational, because many TSAs
/// (including free ones) run their own roots that are not in system stores.
/// </summary>
public sealed record TimestampVerification(
    bool SignatureValid,
    bool ChainTrusted,
    DateTimeOffset? Time,
    string? TsaSubject,
    string? Reason);

/// <summary>
/// RFC 3161 client: ask a Time-Stamp Authority to bind a hash of some bytes to a time it
/// vouches for. Applied to a custody attestation's signature, it closes the gap the examiner's
/// own key leaves open — the examiner cannot backdate the attestation, because the time comes
/// from a third party's clock and signature rather than theirs.
///
/// <para>The request carries a random nonce and asks for the signer's certificates so the
/// token is self-contained; the response is checked against the request (nonce and imprint)
/// before it is accepted. Nothing but the SHA-256 of the data leaves the machine.</para>
/// </summary>
public static class Rfc3161Timestamper
{
    private const int MaxResponseBytes = 1 << 20;

    public static async Task<TimestampResult> TimestampAsync(ReadOnlyMemory<byte> data, Uri tsaUrl, HttpClient http, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tsaUrl);
        ArgumentNullException.ThrowIfNull(http);
        if (tsaUrl.Scheme != Uri.UriSchemeHttps && !tsaUrl.IsLoopback)
        {
            // The token is signed, so transport does not protect its integrity — but a plain
            // http TSA URL is also how a network attacker substitutes a TSA of their choosing.
            throw new ArgumentException("The timestamp authority URL must use https.", nameof(tsaUrl));
        }

        var nonce = RandomNumberGenerator.GetBytes(16);
        nonce[0] &= 0x7F;   // keep the DER INTEGER positive
        nonce[0] |= 0x01;
        var request = Rfc3161TimestampRequest.CreateFromData(data.Span, HashAlgorithmName.SHA256,
            requestedPolicyId: null, nonce: nonce, requestSignerCertificates: true);

        using var content = new ByteArrayContent(request.Encode());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, tsaUrl) { Content = content };
        using var response = await http.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // A TSA reply is a few KB; a server (or an interposed one) must not be able to make
        // us buffer arbitrarily much before the DER parser gets a look at it.
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException("Timestamp response is implausibly large.");
        }
        byte[] body;
        await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            using var ms = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) break;
                if (ms.Length + n > MaxResponseBytes)
                {
                    throw new InvalidDataException("Timestamp response is implausibly large.");
                }
                ms.Write(buffer, 0, n);
            }
            body = ms.ToArray();
        }

        // ProcessResponse rejects a non-granted status and a token whose nonce or imprint
        // differs from the request's, so a replayed or foreign token never gets stored.
        var token = request.ProcessResponse(body, out _);
        var cms = token.AsSignedCms();
        var signer = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0].Certificate : null;
        return new TimestampResult(cms.Encode(), token.TokenInfo.Timestamp, signer?.Subject, token.TokenInfo.PolicyId?.Value);
    }

    public static TimestampVerification Verify(byte[] token, ReadOnlySpan<byte> data)
    {
        if (token is null || !Rfc3161TimestampToken.TryDecode(token, out var decoded, out _))
        {
            return new TimestampVerification(false, false, null, null, "timestamp token does not decode");
        }

        bool valid;
        X509Certificate2? signer;
        try
        {
            valid = decoded.VerifySignatureForData(data, out signer);
        }
        catch (CryptographicException ex)
        {
            return new TimestampVerification(false, false, decoded.TokenInfo.Timestamp, null, $"timestamp signature check failed: {ex.Message}");
        }
        if (!valid)
        {
            return new TimestampVerification(false, false, decoded.TokenInfo.Timestamp, signer?.Subject,
                "timestamp token does not cover this signature, or its own signature does not verify");
        }

        var trusted = false;
        if (signer is not null)
        {
            try
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationTime = decoded.TokenInfo.Timestamp.UtcDateTime;
                trusted = chain.Build(signer);
            }
            catch (CryptographicException) { trusted = false; }
        }
        return new TimestampVerification(true, trusted, decoded.TokenInfo.Timestamp, signer?.Subject,
            trusted ? null : "TSA certificate does not chain to a root this machine trusts (check the issuer against the TSA's published root)");
    }
}
