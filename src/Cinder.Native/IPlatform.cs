namespace Cinder.Native;

/// <summary>
/// The platform abstraction. Concrete implementations live in <c>Cinder.Native.Windows</c> and
/// <c>Cinder.Native.Linux</c>. The rest of the app talks only to this interface so cross-platform
/// code paths stay free of <c>#if</c> noise.
/// </summary>
public interface IPlatform
{
    PlatformInfo Info { get; }

    /// <summary>Open a raw block device (e.g. <c>\\.\PhysicalDrive0</c> or <c>/dev/sda</c>).</summary>
    IRawDevice OpenDevice(string identifier);

    IWriteBlocker GetWriteBlocker();

    IShadowCopyEnumerator GetShadowCopies();

    IReadOnlyList<MountedVolume> EnumerateVolumes();

    /// <summary>Identifier for the OS-native secret store backing API keys, etc.</summary>
    string SecureCredentialStoreId { get; }
}

public sealed record PlatformInfo(string Os, string Architecture, string OsVersion);

public sealed record MountedVolume(string MountPoint, string FsType, long? SizeBytes, bool ReadOnly);
