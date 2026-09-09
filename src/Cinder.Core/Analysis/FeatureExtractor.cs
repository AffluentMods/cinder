using System.Net;
using System.Text.RegularExpressions;

namespace Cinder.Core.Analysis;

/// <summary>One recognised feature inside a piece of text.</summary>
public sealed record FeatureHit(string Preset, string Value, int Index);

/// <summary>A named pattern with an optional semantic validator (Luhn for card numbers, etc.).</summary>
public sealed record FeaturePreset(string Id, string Name, string Description, Regex Pattern, Func<string, bool>? Validate = null)
{
    /// <summary>True when <paramref name="text"/> contains at least one valid instance.</summary>
    public bool IsMatch(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        try
        {
            foreach (Match m in Pattern.Matches(text))
            {
                if (Validate is null || Validate(m.Value))
                {
                    return true;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Treat a pathological input as a non-match rather than stalling the caller.
        }
        return false;
    }
}

/// <summary>
/// bulk_extractor-style feature recognition over extracted strings: emails, URLs, IPs, card
/// numbers, wallet addresses, credentials-shaped tokens. Meant for triage — "what identifiers
/// are in this blob" — not for proof, so every preset errs toward recall and the validators
/// exist to cut the obvious false positives (a 16-digit number that fails Luhn is not a card).
///
/// <para>Every regex carries a match timeout. The inputs are attacker-authored, and a regex
/// that backtracks catastrophically on a crafted string is a hang the examiner cannot cancel.</para>
/// </summary>
public static class FeatureExtractor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static Regex Rx(string pattern) => new(pattern, Opts, Timeout);

    public static IReadOnlyList<FeaturePreset> Presets { get; } =
    [
        new("email", "Email addresses", "user@host.tld",
            Rx(@"\b[A-Z0-9._%+\-]{1,64}@(?:[A-Z0-9\-]{1,63}\.)+[A-Z]{2,24}\b")),

        new("url", "URLs", "http(s)://, ftp://, file://",
            Rx(@"\b(?:https?|ftp|file)://[^\s""'<>\)\]]{3,2048}")),

        new("ipv4", "IPv4 addresses", "dotted quad, each octet 0–255",
            Rx(@"\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b")),

        // Loose shape (hex groups with at least two colons, so compressed `::` forms match),
        // then the runtime parser decides. That rejects clock times, MAC addresses and other
        // colon-separated lookalikes without a regex that tries to encode RFC 4291.
        new("ipv6", "IPv6 addresses", "colon-hex, validated by the runtime parser",
            Rx(@"(?<![:\w.])[0-9A-F]{0,4}(?::[0-9A-F]{0,4}){2,7}(?![:\w.])"),
            v => v.Contains(':') && IPAddress.TryParse(v, out var ip)
              && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6),

        new("domain", "Domain names", "host.tld — at least one dot, alphabetic TLD",
            Rx(@"\b(?:[A-Z0-9](?:[A-Z0-9\-]{0,61}[A-Z0-9])?\.)+(?:[A-Z]{2,24})\b"),
            v => !v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
              && !v.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
              && !v.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
              && !v.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
              && !v.EndsWith(".log", StringComparison.OrdinalIgnoreCase)),

        new("creditcard", "Payment card numbers", "13–19 digits, Luhn-valid",
            Rx(@"\b(?:\d[ \-]?){12,18}\d\b"),
            v => Luhn(v)),

        new("phone", "Phone numbers", "E.164 or common national formats",
            Rx(@"(?<!\d)(?:\+\d{1,3}[ \-.]?)?(?:\(\d{2,4}\)[ \-.]?|\d{2,4}[ \-.])\d{3,4}[ \-.]?\d{3,4}(?!\d)"),
            v => v.Count(char.IsDigit) is >= 8 and <= 15),

        new("bitcoin", "Bitcoin addresses", "Base58 (1…/3…) or Bech32 (bc1…)",
            Rx(@"\b(?:[13][A-HJ-NP-Za-km-z1-9]{25,34}|bc1[AC-HJ-NP-Z02-9]{11,71})\b")),

        new("ethereum", "Ethereum addresses", "0x + 40 hex",
            Rx(@"\b0x[0-9A-F]{40}\b")),

        new("guid", "GUIDs", "8-4-4-4-12 hex",
            Rx(@"\b[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\b")),

        new("winpath", "Windows paths", "drive-letter or UNC paths",
            Rx(@"(?:[A-Z]:\\|\\\\[^\\\s]+\\)[^\s""<>|*?]{1,512}")),

        new("md5", "MD5 hashes", "32 hex", Rx(@"\b[0-9A-F]{32}\b")),
        new("sha1", "SHA-1 hashes", "40 hex", Rx(@"\b[0-9A-F]{40}\b")),
        new("sha256", "SHA-256 hashes", "64 hex", Rx(@"\b[0-9A-F]{64}\b")),

        new("mac", "MAC addresses", "six colon- or dash-separated octets",
            Rx(@"\b(?:[0-9A-F]{2}[:\-]){5}[0-9A-F]{2}\b")),

        new("jwt", "JSON Web Tokens", "eyJ… three base64url segments",
            Rx(@"\beyJ[A-Z0-9_\-]{8,}\.[A-Z0-9_\-]{8,}\.[A-Z0-9_\-]{8,}\b")),

        new("awskey", "AWS access key IDs", "AKIA/ASIA + 16 uppercase alnum",
            Rx(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")),

        new("privatekey", "Private key blocks", "-----BEGIN … PRIVATE KEY-----",
            Rx(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----")),

        new("base64", "Base64 blobs", "≥ 40 chars that decode cleanly",
            Rx(@"(?<![A-Z0-9+/=])(?:[A-Z0-9+/]{4}){10,}(?:[A-Z0-9+/]{2}==|[A-Z0-9+/]{3}=)?(?![A-Z0-9+/=])"),
            v => Convert.TryFromBase64String(v, new byte[v.Length], out _)),
    ];

    public static FeaturePreset? Find(string id) =>
        Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every valid hit for one preset in <paramref name="text"/>.</summary>
    public static IEnumerable<FeatureHit> Extract(string text, FeaturePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        MatchCollection matches;
        try
        {
            matches = preset.Pattern.Matches(text);
            // Force evaluation inside the try so a timeout surfaces here, not in the iterator.
            _ = matches.Count;
        }
        catch (RegexMatchTimeoutException)
        {
            yield break;
        }

        foreach (Match m in matches)
        {
            if (preset.Validate is null || preset.Validate(m.Value))
            {
                yield return new FeatureHit(preset.Id, m.Value, m.Index);
            }
        }
    }

    /// <summary>Every valid hit for every preset.</summary>
    public static IEnumerable<FeatureHit> ExtractAll(string text) =>
        Presets.SelectMany(p => Extract(text, p));

    /// <summary>
    /// Luhn check over the digits of <paramref name="candidate"/>, ignoring separators. Returns
    /// false for fewer than 13 digits (no card network issues shorter PANs) and for runs of a
    /// single repeated digit, which pass Luhn trivially and are never real cards.
    /// </summary>
    public static bool Luhn(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }
        if (digits.All(d => d == digits[0]))
        {
            return false;
        }

        var sum = 0;
        var alternate = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i];
            if (alternate)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }
            sum += d;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }
}
