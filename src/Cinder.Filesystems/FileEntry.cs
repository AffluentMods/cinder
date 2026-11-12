namespace Cinder.Filesystems;

/// <summary>One entry in a parsed filesystem — the filesystem-agnostic view returned by every
/// parser sidecar. Phase 3 ships the basic field set; later phases extend with FS-specific
/// extras (ADS streams on NTFS, xattrs on ext, etc.).</summary>
public sealed record FileEntry(
    long Inode,
    string Path,
    string Name,
    long Size,
    bool IsDirectory,
    bool IsDeleted,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? ModifiedUtc,
    DateTimeOffset? AccessedUtc,
    DateTimeOffset? MetadataChangedUtc,
    string? Owner,
    string? Group,
    int? UnixMode,
    IReadOnlyDictionary<string, string>? Extras = null)
{
    /// <summary>
    /// True when this entry's timestamp fields are <em>not</em> UTC instants despite their
    /// names: the filesystem (FAT) records local wall-clock time and no zone, so the fields
    /// hold that reading exactly as written, with a placeholder offset of +00:00. Show such a
    /// value as "local time, zone not recorded" — never with a "Z" or "UTC" suffix, and never
    /// converted. Set by the walker through <c>Extras["time_basis"] = "local-unknown-zone"</c>.
    /// </summary>
    public bool TimesAreLocalWallClock =>
        Extras is not null
        && Extras.TryGetValue(DiscUtilsWalker.TimeBasisKey, out var basis)
        && basis == DiscUtilsWalker.TimeBasisLocalUnknownZone;
}
