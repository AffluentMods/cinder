using System.Text.RegularExpressions;

namespace Cinder.Core.Analysis;

public enum IndicatorType { Md5, Sha1, Sha256, Ipv4, Ipv6, Domain, Url, Email, Text }

/// <summary>One indicator of compromise: the value as written, and what kind it was recognised as.</summary>
public sealed record Indicator(string Value, IndicatorType Type, int Line)
{
    public bool IsHash => Type is IndicatorType.Md5 or IndicatorType.Sha1 or IndicatorType.Sha256;
}

/// <summary>
/// Parses a plain IOC list — one indicator per line, <c>#</c> or <c>//</c> comments, blank lines
/// ignored — and classifies each line by shape so the scanner knows whether to compare it
/// against file hashes, look for it in bytes, or both. Anything unrecognised is a literal
/// string to search for, which is the right default for a filename, a mutex name or a user.
/// </summary>
public static class IocList
{
    private static readonly Regex Hex32 = new("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex Hex40 = new("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex Hex64 = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<Indicator> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<Indicator>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineNo = 0;

        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim().TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            // Tolerate "value  # comment" and "type,value" / "value,description" CSV-ish rows by
            // taking the first token that classifies as something other than free text.
            var candidate = StripTrailingComment(line);
            var value = PickValue(candidate);
            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }
            result.Add(new Indicator(value, Classify(value), lineNo));
        }
        return result;
    }

    public static IndicatorType Classify(string value)
    {
        if (Hex32.IsMatch(value)) return IndicatorType.Md5;
        if (Hex40.IsMatch(value)) return IndicatorType.Sha1;
        if (Hex64.IsMatch(value)) return IndicatorType.Sha256;

        if (Whole("ipv4", value)) return IndicatorType.Ipv4;
        if (Whole("ipv6", value)) return IndicatorType.Ipv6;
        if (Whole("url", value)) return IndicatorType.Url;
        if (Whole("email", value)) return IndicatorType.Email;
        if (Whole("domain", value)) return IndicatorType.Domain;
        return IndicatorType.Text;
    }

    /// <summary>True when the preset matches the entire value, not a substring of it.</summary>
    private static bool Whole(string presetId, string value)
    {
        var preset = FeatureExtractor.Find(presetId);
        if (preset is null)
        {
            return false;
        }
        try
        {
            var m = preset.Pattern.Match(value);
            return m.Success && m.Index == 0 && m.Length == value.Length
                && (preset.Validate is null || preset.Validate(value));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string StripTrailingComment(string line)
    {
        var hash = line.IndexOf(" #", StringComparison.Ordinal);
        return hash > 0 ? line[..hash].Trim() : line;
    }

    private static string PickValue(string line)
    {
        if (!line.Contains(',') && !line.Contains('\t'))
        {
            return line;
        }
        var parts = line.Split([',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            var unquoted = p.Trim('"');
            if (unquoted.Length > 0 && Classify(unquoted) != IndicatorType.Text)
            {
                return unquoted;
            }
        }
        return parts.Length > 0 ? parts[0].Trim('"') : line;
    }
}
