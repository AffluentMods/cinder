using Cinder.Core.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// The property that matters here is a negative one: a program planted in the application's own
/// directory must never be selected. Windows resolves a bare <c>ProcessStartInfo.FileName</c>
/// against the directory the running executable was loaded from, ahead of the system directory
/// and ahead of PATH — so for a portable forensics tool that examiners keep next to their case
/// files, a bare name is a code-execution path.
/// </summary>
public sealed class ExecutableResolverTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-exeres").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Does_not_resolve_a_program_planted_in_the_application_directory()
    {
        // AppContext.BaseDirectory is the test host's own directory. Plant a uniquely named
        // file there and confirm the resolver refuses to find it — PATH does not contain it,
        // and the application directory must not be searched.
        var planted = Path.Combine(
            AppContext.BaseDirectory,
            "cinder-planted-probe-" + Guid.NewGuid().ToString("N") + ".exe");

        File.WriteAllBytes(planted, [0x4D, 0x5A]);
        try
        {
            ExecutableResolver.Resolve(Path.GetFileName(planted))
                .Should().BeNull("the application directory must never be searched");
        }
        finally
        {
            try { File.Delete(planted); } catch { }
        }
    }

    [Fact]
    public void Does_not_resolve_a_program_in_the_current_working_directory()
    {
        var planted = Path.Combine(_dir, "cinder-cwd-probe-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(planted, [0x4D, 0x5A]);

        var original = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_dir);
            ExecutableResolver.Resolve(Path.GetFileName(planted))
                .Should().BeNull("the working directory must never be searched");
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    [Fact]
    public void Finds_a_program_on_PATH()
    {
        var name = "cinder-path-probe-" + Guid.NewGuid().ToString("N") + ".exe";
        File.WriteAllBytes(Path.Combine(_dir, name), [0x4D, 0x5A]);

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", _dir + Path.PathSeparator + originalPath);
            var resolved = ExecutableResolver.Resolve(name);
            resolved.Should().NotBeNull();
            Path.IsPathRooted(resolved).Should().BeTrue("callers pass the result straight to Process.Start");
            resolved.Should().Be(Path.Combine(_dir, name));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void Ignores_dot_entries_on_PATH()
    {
        // "." on PATH means the working directory. Honouring it would reintroduce the very
        // ambiguity this resolver exists to remove, so it is skipped even when the user set it.
        var name = "cinder-dot-probe-" + Guid.NewGuid().ToString("N") + ".exe";
        File.WriteAllBytes(Path.Combine(_dir, name), [0x4D, 0x5A]);

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_dir);
            Environment.SetEnvironmentVariable("PATH", ".");
            ExecutableResolver.Resolve(name).Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public void Honours_an_absolute_path_the_user_configured()
    {
        var explicitPath = Path.Combine(_dir, "explicit-python.exe");
        File.WriteAllBytes(explicitPath, [0x4D, 0x5A]);

        ExecutableResolver.Resolve(explicitPath).Should().Be(explicitPath);
        ExecutableResolver.Resolve(Path.Combine(_dir, "missing.exe")).Should().BeNull();
    }

    [Fact]
    public void Rejects_relative_paths_containing_separators()
    {
        // `.\python.exe` or `tools/python.exe` would resolve against the working directory.
        ExecutableResolver.Resolve("./python").Should().BeNull();
        ExecutableResolver.Resolve("sub/python").Should().BeNull();
        ExecutableResolver.Resolve("sub\\python").Should().BeNull();
    }

    [Fact]
    public void Resolves_a_known_system_tool_on_the_current_platform()
    {
        var resolved = OperatingSystem.IsWindows()
            ? ExecutableResolver.Resolve("cmd.exe")
            : ExecutableResolver.Resolve("sh");

        resolved.Should().NotBeNull();
        File.Exists(resolved).Should().BeTrue();
    }

    [Fact]
    public void ResolveRequired_throws_a_message_naming_the_program()
    {
        var act = () => ExecutableResolver.ResolveRequired(
            "cinder-definitely-not-installed-" + Guid.NewGuid().ToString("N"),
            "Install it first.");

        act.Should().Throw<FileNotFoundException>()
            .WithMessage("*cinder-definitely-not-installed*")
            .WithMessage("*Install it first.*");
    }

    [Fact]
    public void Rejects_empty_input()
    {
        var act = () => ExecutableResolver.Resolve("  ");
        act.Should().Throw<ArgumentException>();
    }
}
