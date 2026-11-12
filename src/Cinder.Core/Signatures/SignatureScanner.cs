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

        // A magic number identifies a container, and most containers are stored under more
        // than one extension: every DLL starts with MZ, every .xls is a compound file, every
        // .wav is RIFF. Comparing against the single canonical extension of the best hit
        // flagged all of those as mismatches. The extension is consistent with the content
        // when it is the canonical extension, or a known alias, of ANY signature that matched.
        foreach (var hit in hits)
        {
            if (IsExtensionConsistent(ext, hit.Signature.Extension))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// True when a file named <c>*.<paramref name="extension"/></c> is an ordinary home for
    /// content whose signature has the canonical extension <paramref name="canonical"/>.
    /// Both are compared case-insensitively, without a leading dot.
    /// </summary>
    public static bool IsExtensionConsistent(string extension, string canonical)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(canonical);
        var ext = extension.TrimStart('.').ToLower(CultureInfo.InvariantCulture);
        var canon = canonical.TrimStart('.').ToLower(CultureInfo.InvariantCulture);
        if (ext == canon)
        {
            return true;
        }
        if (Aliases.TryGetValue(canon, out var set) && set.Contains(ext))
        {
            return true;
        }

        // EWF segment files: image.E01, image.E02 … image.EAA all carry the same magic.
        return canon == "e01" && ext.Length == 3 && ext[0] == 'e'
            && char.IsAsciiLetterOrDigit(ext[1]) && char.IsAsciiLetterOrDigit(ext[2]);
    }

    private static readonly string[] ZipBased =
    [
        "zip", "docx", "docm", "dotx", "dotm", "xlsx", "xlsm", "xltx", "xltm", "xlsb", "pptx", "pptm", "ppsx", "potx",
        "vsdx", "odt", "ods", "odp", "odg", "odf", "ott", "epub", "jar", "war", "ear", "apk", "aab", "aar", "ipa",
        "xpi", "vsix", "nupkg", "whl", "kmz", "appx", "msix", "appxbundle", "msixbundle", "xps", "oxps", "cbz",
        "zipx", "3mf", "usdz",
    ];

    private static readonly string[] RawImage =
        ["dd", "img", "raw", "bin", "001", "ima", "image", "dmg", "flp", "vfd", "part"];

    /// <summary>Canonical signature extension → the other extensions the same content is normally stored under.</summary>
    private static readonly Dictionary<string, HashSet<string>> Aliases = new(StringComparer.Ordinal)
    {
        // Images
        ["jpg"] = ["jpeg", "jpe", "jfif", "jif"],
        ["tiff"] = ["tif", "dng", "nef", "cr2", "arw"],
        ["bmp"] = ["dib"],
        ["heic"] = ["heif", "hif"],
        ["avif"] = ["avifs"],

        // Containers: ISO-BMFF, Matroska, RIFF, Ogg
        ["mp4"] = ["mov", "qt", "m4a", "m4v", "m4b", "m4p", "3gp", "3g2", "f4v", "heic", "heif", "avif", "cr3"],
        ["mkv"] = ["webm", "mka", "mks", "mk3d"],
        ["avi"] = ["wav", "wave", "webp", "ani", "rmi", "cda"],
        ["ogg"] = ["oga", "ogv", "ogx", "opus", "spx"],

        // Archives
        ["zip"] = [.. ZipBased],
        ["docx"] = [.. ZipBased],
        ["gz"] = ["tgz", "taz", "svgz", "emz"],
        ["bz2"] = ["tbz", "tbz2", "bz"],
        ["xz"] = ["txz"],
        ["zst"] = ["tzst", "zstd"],

        // Documents
        ["doc"] = ["xls", "ppt", "pps", "pot", "dot", "xlt", "xla", "msi", "msp", "msm", "mst", "msg", "oft", "vsd", "pub", "mpp", "db", "suo"],
        ["pdf"] = ["ai"],

        // Executables
        ["exe"] = ["dll", "sys", "scr", "ocx", "cpl", "drv", "efi", "mui", "ax", "acm", "com", "winmd", "pyd", "node", "xll"],
        ["elf"] = ["so", "o", "ko", "axf", "out", "bin"],
        ["macho"] = ["dylib", "bundle", "o"],

        // Disk images and raw filesystems
        ["vhdx"] = ["avhdx"],
        ["qcow"] = ["qcow2", "img"],
        ["ntfs"] = [.. RawImage],
        ["fat"] = [.. RawImage],
        ["ext"] = [.. RawImage],
        ["apfs"] = [.. RawImage],
        ["hfsplus"] = [.. RawImage],
        ["btrfs"] = [.. RawImage],
        ["xfs"] = [.. RawImage],
        ["iso"] = ["img", "udf", "bin", "dd", "raw"],

        // Databases and Windows artifacts
        ["sqlite"] = ["db", "sqlite3", "db3", "s3db", "sl3", "sqlitedb"],
        ["pst"] = ["ost"],
        ["ost"] = ["pst"],
        ["ldb"] = ["log"],
        ["hive"] = ["dat", "hiv", "hve", "sav", "bak", "man"],
        ["hib"] = ["sys"],

        // Network captures
        ["pcap"] = ["cap", "dmp"],
        ["pcapng"] = ["pcap", "cap", "ntar"],
    };
}
