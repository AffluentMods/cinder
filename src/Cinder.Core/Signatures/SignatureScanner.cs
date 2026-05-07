using System.Globalization;

namespace Cinder.Core.Signatures;

public sealed record SignatureMatch(MagicSignature Signature, long Offset);

/// <summary>
/// Scans a header buffer against a registry of <see cref="MagicSignature"/> entries.
/// Used by the hex viewer to badge files with a "true type" and to surface
/// extension-vs-content mismatches.
/// </summary>
public sealed class SignatureScanner
{
    private readonly IReadOnlyList<MagicSignature> _signatures;

    public SignatureScanner(IReadOnlyList<MagicSignature>? signatures = null)
    {
        _signatures = signatures ?? MagicSignatures.All;
    }

    public IReadOnlyList<SignatureMatch> Scan(ReadOnlySpan<byte> header)
    {
        var hits = new List<SignatureMatch>();
        foreach (var sig in _signatures)
        {
            if (sig.Matches(header))
            {
                hits.Add(new SignatureMatch(sig, sig.Offset));
            }
        }
        return hits;
    }

    /// <summary>True if the file's extension disagrees with its actual signature.</summary>
    public bool IsExtensionMismatch(string fileName, ReadOnlySpan<byte> header, out SignatureMatch? bestMatch)
    {
        bestMatch = null;
        var hits = Scan(header);
        if (hits.Count == 0)
        {
            return false;
        }

        // Most authoritative match: the lowest-offset, longest-pattern hit.
        bestMatch = hits.OrderBy(h => h.Signature.Offset).ThenByDescending(h => h.Signature.Pattern.Length).First();

        var ext = Path.GetExtension(fileName).TrimStart('.').ToLower(CultureInfo.InvariantCulture);
        if (ext.Length == 0)
        {
            return false;
        }

        // ZIP-derived OOXML/ODF extensions all share the PK magic — accept any of them.
        if (bestMatch.Signature.Extension == "zip" &&
            ext is "docx" or "xlsx" or "pptx" or "odt" or "ods" or "odp" or "epub" or "jar" or "apk")
        {
            return false;
        }

        return !string.Equals(ext, bestMatch.Signature.Extension, StringComparison.OrdinalIgnoreCase);
    }
}
