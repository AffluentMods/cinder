using System.Runtime.Versioning;

namespace Cinder.Native.Windows;

/// <summary>
/// Windows <see cref="IPlatform"/>. Phase 0 stub — concrete imaging / VSS / write-blocker
/// implementations land in Phase 2. The interface surface is in place so DI wiring in the shell
/// is platform-aware from day one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IPlatform
{
    public PlatformInfo Info { get; } = new(
        Os: "Windows",
        Architecture: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        OsVersion: Environment.OSVersion.VersionString);

    public string SecureCredentialStoreId => "dpapi";

    public IRawDevice OpenDevice(string identifier) =>
        throw new NotImplementedException("Windows raw-device IO arrives in Phase 2.");

    public IWriteBlocker GetWriteBlocker() => new InertWriteBlocker();

    public IShadowCopyEnumerator GetShadowCopies() => new EmptyShadowCopyEnumerator();

    public IReadOnlyList<MountedVolume> EnumerateVolumes()
    {
        var drives = DriveInfo.GetDrives();
        var list = new List<MountedVolume>(drives.Length);
        foreach (var d in drives)
        {
            try
            {
                if (!d.IsReady)
                {
                    continue;
                }
                list.Add(new MountedVolume(d.RootDirectory.FullName, d.DriveFormat, d.TotalSize, ReadOnly: false));
            }
            catch (IOException)
            {
                // Skip unreadable drives.
            }
            catch (UnauthorizedAccessException)
            {
                // Skip drives the current user can't query.
            }
        }
        return list;
    }

    private sealed class InertWriteBlocker : IWriteBlocker
    {
        public bool IsActive => false;
        public bool TryEngage() => false;
        public bool TryDisengage() => false;
    }

    private sealed class EmptyShadowCopyEnumerator : IShadowCopyEnumerator
    {
        public IReadOnlyList<ShadowCopy> Enumerate() => [];
    }
}
