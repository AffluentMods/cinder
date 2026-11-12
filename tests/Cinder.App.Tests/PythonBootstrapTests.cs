using System.Diagnostics;
using Cinder.App.Services;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

public class PythonBootstrapTests
{
    private sealed class Collect : IProgress<string>
    {
        public List<string> Lines { get; } = [];

        public void Report(string value)
        {
            lock (Lines)
            {
                Lines.Add(value);
            }
        }
    }

    /// <summary>A child process that runs for about a minute unless something stops it.</summary>
    private static (string File, string[] Args) LongRunning() =>
        OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "ping.exe"), ["-n", "60", "127.0.0.1"])
            : ("/bin/sleep", ["60"]);

    [Fact]
    public async Task Cancelling_kills_the_child_process_instead_of_abandoning_it()
    {
        var (file, args) = LongRunning();
        if (!File.Exists(file))
        {
            return;   // an image without ping / sleep: nothing to exercise
        }

        using var cts = new CancellationTokenSource();
        var pid = 0;
        var run = PythonBootstrap.RunAsync(file, args, new Collect(), cts.Token, started => pid = started);

        // Let it get going, then cancel the way a Cancel button does.
        await Task.Delay(500);
        pid.Should().NotBe(0);
        IsRunning(pid).Should().BeTrue("the child has to be alive for the cancellation to mean anything");
        cts.Cancel();

        var act = () => run;
        await act.Should().ThrowAsync<OperationCanceledException>();

        var deadline = Stopwatch.StartNew();
        while (IsRunning(pid) && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
        }
        IsRunning(pid).Should().BeFalse("a cancelled install must not keep running in the background");
    }

    [Fact]
    public async Task A_process_that_finishes_reports_its_exit_code_and_output()
    {
        var dotnet = Environment.ProcessPath;
        if (dotnet is null || !Path.GetFileNameWithoutExtension(dotnet).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Run under a test host that is not the dotnet muxer; fall back to PATH.
            dotnet = "dotnet";
        }
        var progress = new Collect();

        var ok = await PythonBootstrap.RunAsync(dotnet, ["--version"], progress, CancellationToken.None);

        ok.Should().BeTrue();
        lock (progress.Lines)
        {
            progress.Lines.Should().Contain(line => line.Length > 0 && char.IsAsciiDigit(line[0]));
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;   // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
