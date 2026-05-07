namespace Cinder.Native;

/// <summary>Enumerates VSS snapshots (Windows) or btrfs/LVM/ZFS snapshots (Linux).</summary>
public interface IShadowCopyEnumerator
{
    IReadOnlyList<ShadowCopy> Enumerate();
}

public sealed record ShadowCopy(string Id, string Origin, DateTimeOffset CreatedUtc, string? Notes);
