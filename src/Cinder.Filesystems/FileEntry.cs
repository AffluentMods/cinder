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
    IReadOnlyDictionary<string, string>? Extras = null);
