namespace Cinder.Filesystems;

public enum FilesystemKind
{
    Unknown, Ntfs, Fat12, Fat16, Fat32, ExFat, Ext2, Ext3, Ext4, Apfs, HfsPlus, Refs, Udf, Iso9660,
    Btrfs, Zfs, Xfs, F2fs, ReiserFs, Squashfs,
}

public sealed record FilesystemInfo(FilesystemKind Kind, string? Label, long? VolumeSize, int? ClusterSize, IReadOnlyDictionary<string, string>? Extras = null);

public interface IFilesystemParser
{
    Task<FilesystemInfo> IdentifyAsync(string imagePath, long offsetBytes = 0, CancellationToken ct = default);
    IAsyncEnumerable<FileEntry> EnumerateAsync(string imagePath, long offsetBytes = 0, CancellationToken ct = default);
    Task<byte[]> ReadFileAsync(string imagePath, long inode, long offsetBytes = 0, CancellationToken ct = default);
}
