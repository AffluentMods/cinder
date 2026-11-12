using System.Text;

namespace Cinder.App.Services;

/// <summary>
/// Whole-file replacement that cannot leave a half-written file behind. The new content goes to
/// a temporary file in the same directory, is flushed to disk, and is then renamed over the
/// target — so a reader (or a crash, or a second Cinder instance) sees either the old file or
/// the new one, never a truncated mixture.
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="contents"/> (UTF-8, no BOM).
    /// With <paramref name="ownerOnly"/> the file is created readable and writable by the
    /// owning user alone on Unix, from its first byte — there is no window in which it exists
    /// with wider permissions. Throws on failure, leaving the existing file untouched.
    /// </summary>
    public static void WriteAllText(string path, string contents, bool ownerOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(contents);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (ownerOnly && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temp, options))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // Nothing more to do: the original file is intact, which is what matters.
            }
            throw;
        }
    }
}
