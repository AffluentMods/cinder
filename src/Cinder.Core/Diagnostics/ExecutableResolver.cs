using System.Runtime.InteropServices;

namespace Cinder.Core.Diagnostics;

/// <summary>
/// Resolves a helper program's name to an absolute path before it is handed to
/// <see cref="System.Diagnostics.Process"/>.
///
/// <para><b>Why this exists.</b> Passing a bare name such as <c>"python.exe"</c> as
/// <c>ProcessStartInfo.FileName</c> lets Windows pick the executable, and the search order it
/// uses begins with <em>the directory the running application was loaded from</em> — ahead of
/// the system directory and ahead of PATH. Cinder ships as a portable single-file executable
/// that examiners keep wherever the case lives, and it processes files an adversary authored.
/// A <c>python.exe</c> sitting next to <c>Cinder.exe</c> is therefore executed in preference to
/// the real interpreter, with whatever privileges Cinder holds — which the documentation asks
/// to be Administrator.</para>
///
/// <para>This resolver searches only locations the user controls deliberately: the Windows
/// system directory, then PATH. It never consults the application directory or the process
/// working directory. A name that is already rooted is returned unchanged, so a path the user
/// configured explicitly still wins.</para>
/// </summary>
public static class ExecutableResolver
{
    /// <summary>
    /// Returns an absolute path for <paramref name="program"/>, or null when it cannot be found.
    /// </summary>
    public static string? Resolve(string program)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);

        // An explicit path from configuration is the user's decision — honour it.
        if (Path.IsPathRooted(program))
        {
            return File.Exists(program) ? program : null;
        }

        // Reject anything with a directory separator: a relative path like `.\python.exe` or
        // `tools/python.exe` would reintroduce exactly the ambiguity this class removes.
        if (program.Contains('/') || program.Contains('\\'))
        {
            return null;
        }

        foreach (var candidateName in CandidateNames(program))
        {
            foreach (var dir in SearchDirectories())
            {
                string full;
                try
                {
                    full = Path.Combine(dir, candidateName);
                }
                catch (ArgumentException)
                {
                    continue;   // malformed PATH entry
                }

                if (File.Exists(full))
                {
                    return full;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Same as <see cref="Resolve"/> but throws a message the UI can show verbatim when the
    /// program is missing, rather than letting Win32 error 2 surface from Process.Start.
    /// </summary>
    public static string ResolveRequired(string program, string? installHint = null)
    {
        return Resolve(program)
            ?? throw new FileNotFoundException(
                $"'{program}' was not found on PATH." +
                (installHint is null ? "" : " " + installHint));
    }

    /// <summary>
    /// Directories searched, in order. Deliberately excludes <c>AppContext.BaseDirectory</c>
    /// and <c>Environment.CurrentDirectory</c>.
    /// </summary>
    private static IEnumerable<string> SearchDirectories()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // System tools (powershell, vssadmin, whoami, …) resolve here first so a copy
            // planted earlier on PATH cannot shadow them.
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrEmpty(system32))
            {
                yield return system32;
            }
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows))
            {
                yield return windows;
            }
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in pathVar.Split(Path.PathSeparator))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0)
            {
                continue;
            }
            // "." and "" on PATH mean the working directory. Honouring them here would undo
            // the point of this class, so they are skipped even when the user set them.
            if (dir == "." || dir == "..")
            {
                continue;
            }
            yield return dir;
        }
    }

    /// <summary>On Windows, try the bare name then each PATHEXT suffix.</summary>
    private static IEnumerable<string> CandidateNames(string program)
    {
        yield return program;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || Path.HasExtension(program))
        {
            yield break;
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        foreach (var ext in pathExt.Split(';'))
        {
            var e = ext.Trim();
            if (e.Length > 1 && e[0] == '.')
            {
                yield return program + e;
            }
        }
    }
}
