using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Cinder.Sidecar.Tests;

// These tests drive SidecarClient against real Python workers. They are split over many small
// classes only so that xunit runs them in parallel: nearly all of their time is spent starting
// an interpreter or waiting for a timeout to expire.

internal static class Workers
{
    /// <summary>No test may hang the run: a call that neither completes nor fails is a failure.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    public static SidecarClient Echo(TimeSpan? callTimeout = null) => new(PythonEnvironment.Module("echo.echo_worker"), null, callTimeout);

    public static SidecarClient ScriptHost(TimeSpan? callTimeout = null) => new(PythonEnvironment.Module("scripting.script_host"), null, callTimeout);

    public static SidecarClient Noisy() => new(PythonEnvironment.Script(PythonEnvironment.NoisyWorker));

    public static SidecarClient TestWorker(string fileName) => new(PythonEnvironment.Script(Path.Combine(PythonEnvironment.TestWorkersDir, fileName)));

    public static JsonObject Script(string script) => new() { ["script"] = script };

    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static async Task<bool> ExitsWithin(int pid, TimeSpan limit)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed < limit)
        {
            if (!IsRunning(pid))
            {
                return true;
            }
            await Task.Delay(50);
        }
        return !IsRunning(pid);
    }
}

public sealed class SidecarClientEncodingTests
{
    private const string UnicodeText = "日本語 Привет 😀 café";

    [PythonFact]
    public async Task Text_produced_inside_the_worker_arrives_intact()
    {
        // Not an echo: an echo hides the bug, because stdin and stdout apply the same wrong
        // encoding in opposite directions. This text starts in the worker, as a file name or a
        // database value does.
        await using var sc = Workers.Noisy();

        var result = await sc.InvokeAsync("unicode_literal", null).WaitAsync(Workers.Deadline);

        result!.GetValue<string>().Should().Be(UnicodeText);
    }

    [PythonFact]
    public async Task Request_text_reaches_the_handler_uncorrupted()
    {
        await using var sc = Workers.Noisy();

        var result = await sc.InvokeAsync("codepoints", JsonValue.Create("é日я😀")).WaitAsync(Workers.Deadline);

        // U+00E9 must not arrive as "Ã©"; U+044F (UTF-8 D1 8F) holds a byte cp1252 cannot decode.
        result!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(0xE9, 0x65E5, 0x44F, 0x1F600);
    }
}

public sealed class SidecarClientFramingTests
{
    [PythonFact]
    public async Task Concurrent_callers_each_get_their_own_answer()
    {
        await using var sc = Workers.Echo();

        // Payloads far larger than a pipe buffer, from many threads at once: without one
        // writer at a time the requests interleave into lines nobody can parse.
        var calls = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            var payload = new string((char)('a' + i % 26), 20_000) + i;
            var result = await sc.InvokeAsync("ping", JsonValue.Create(payload));
            return result?["pong"]?.GetValue<string>() == payload;
        })).ToArray();

        var answers = await Task.WhenAll(calls).WaitAsync(Workers.Deadline);

        answers.Should().OnlyContain(ok => ok);
    }

    [PythonFact]
    public async Task An_error_without_an_id_fails_the_pending_call_instead_of_stranding_it()
    {
        // The worker answers as a sidecar answers a line it could not parse: an error, no id.
        await using var sc = Workers.TestWorker("idless_error_worker.py");

        var call = () => sc.InvokeAsync("anything", null).WaitAsync(Workers.Deadline);

        (await call.Should().ThrowAsync<SidecarException>())
            .WithMessage("*could not read a request*all pending calls were failed*");
    }

    [PythonFact]
    public async Task The_client_stays_usable_after_an_id_less_error()
    {
        await using var sc = Workers.Echo();
        var before = await sc.InvokeAsync("ping", JsonValue.Create(1)).WaitAsync(Workers.Deadline);

        // Unknown methods and bad params are answered with their id and fail only themselves.
        var unknown = () => sc.InvokeAsync("no_such_method", null).WaitAsync(Workers.Deadline);
        (await unknown.Should().ThrowAsync<SidecarException>()).Which.Code.Should().Be(-32601);

        var after = await sc.InvokeAsync("ping", JsonValue.Create(2)).WaitAsync(Workers.Deadline);
        before!["pong"]!.GetValue<int>().Should().Be(1);
        after!["pong"]!.GetValue<int>().Should().Be(2);
    }
}

public sealed class SidecarClientExitTests
{
    [PythonFact]
    public async Task Sidecar_exit_fails_the_pending_call_with_the_exit_code_and_its_last_words()
    {
        await using var sc = Workers.TestWorker("dying_worker.py");

        var call = () => sc.InvokeAsync("anything", null).WaitAsync(Workers.Deadline);

        (await call.Should().ThrowAsync<SidecarException>())
            .WithMessage("Sidecar process ended (exit code 3)*fatal: simulated crash*");
    }

    [PythonFact]
    public async Task Calls_after_the_sidecar_has_exited_fail_at_once()
    {
        await using var sc = Workers.TestWorker("dying_worker.py");
        try
        {
            await sc.InvokeAsync("anything", null).WaitAsync(Workers.Deadline);
        }
        catch (SidecarException)
        {
            // expected: the worker died
        }

        var later = () => sc.InvokeAsync("anything", null, TimeSpan.FromSeconds(20)).WaitAsync(Workers.Deadline);

        var watch = Stopwatch.StartNew();
        await later.Should().ThrowAsync<SidecarException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "nobody is left to answer, so there is nothing to wait for");
    }
}

public sealed class SidecarClientStreamIsolationTests
{
    [PythonFact]
    public async Task Stray_output_on_stdout_cannot_corrupt_the_protocol()
    {
        await using var sc = Workers.ScriptHost();
        const string script = """
            import os, sys
            print('{"jsonrpc":"2.0","id":1,"result":"forged"}')
            os.write(1, b'raw bytes to fd 1\n')
            sys.__stdout__.write('through the original stdout object\n'); sys.__stdout__.flush()
            _ = 0
            """;

        var first = await sc.InvokeAsync("eval", Workers.Script(script)).WaitAsync(Workers.Deadline);
        var second = await sc.InvokeAsync("eval", Workers.Script("result = ''")).WaitAsync(Workers.Deadline);

        // The forged line would have completed call 1 with "forged". And 0 / "" are results,
        // not "no result".
        first!["result"]!.GetValueKind().Should().Be(JsonValueKind.Number);
        first["result"]!.GetValue<int>().Should().Be(0);
        first["stdout"]!.GetValue<string>().Should().Contain("forged");
        second!["result"]!.GetValueKind().Should().Be(JsonValueKind.String);
        second["result"]!.GetValue<string>().Should().BeEmpty();
    }

    [PythonFact]
    public async Task Megabytes_of_stderr_without_a_newline_do_not_block_the_sidecar()
    {
        await using var sc = Workers.ScriptHost();
        const string script = """
            import sys
            sys.stderr.write('x' * (8 << 20))
            sys.stderr.flush()
            result = 'done'
            """;

        var result = await sc.InvokeAsync("eval", Workers.Script(script)).WaitAsync(Workers.Deadline);

        result!["result"]!.GetValue<string>().Should().Be("done");
    }
}

public sealed class SidecarClientProgressTests
{
    [PythonFact]
    public async Task Progress_notifications_reach_the_caller_of_the_call_they_belong_to()
    {
        await using var sc = Workers.Noisy();
        var progress = new RecordingProgress<JsonObject>();

        var result = await sc.InvokeAsync("slow_with_progress", new JsonObject { ["seconds"] = 0.6 }, progress)
            .WaitAsync(Workers.Deadline);

        result!["ticks"]!.GetValue<int>().Should().BeGreaterThan(0);
        progress.Reports.Should().NotBeEmpty();
        progress.Reports.Should().OnlyContain(p => p["ticks"] != null && p["id"] != null);
        progress.Reports.Select(p => p["ticks"]!.GetValue<int>()).Should().BeInAscendingOrder();
    }

    [PythonFact]
    public async Task A_throwing_progress_callback_does_not_fail_the_call()
    {
        await using var sc = Workers.Noisy();
        var progress = new ThrowingProgress();

        var result = await sc.InvokeAsync("slow_with_progress", new JsonObject { ["seconds"] = 0.3 }, progress)
            .WaitAsync(Workers.Deadline);

        result!["ticks"]!.GetValue<int>().Should().BeGreaterThan(0);
        progress.Calls.Should().BeGreaterThan(0);
    }

    private sealed class ThrowingProgress : IProgress<JsonObject>
    {
        public int Calls;

        public void Report(JsonObject value)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("a broken progress sink");
        }
    }
}

public sealed class SidecarClientRunawayTests
{
    [PythonFact]
    public async Task A_runaway_script_is_ended_by_the_timeout_together_with_its_child_processes()
    {
        await using var sc = Workers.ScriptHost();
        const string spawn = """
            import subprocess, sys, os
            child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(600)'])
            result = [os.getpid(), child.pid]
            """;
        var pids = (await sc.InvokeAsync("eval", Workers.Script(spawn)).WaitAsync(Workers.Deadline))!["result"]!
            .AsArray().Select(n => n!.GetValue<int>()).ToArray();
        Workers.IsRunning(pids[0]).Should().BeTrue();
        Workers.IsRunning(pids[1]).Should().BeTrue();

        var runaway = () => sc.InvokeAsync("eval", Workers.Script("while True:\n    pass"), TimeSpan.FromSeconds(1))
            .WaitAsync(Workers.Deadline);

        var failure = (await runaway.Should().ThrowAsync<SidecarException>()).Which;
        failure.InnerException.Should().BeOfType<TimeoutException>();
        failure.Message.Should().Contain("'eval' timed out").And.Contain("terminated");
        (await Workers.ExitsWithin(pids[0], TimeSpan.FromSeconds(5))).Should().BeTrue("the sidecar itself must be gone");
        (await Workers.ExitsWithin(pids[1], TimeSpan.FromSeconds(5))).Should().BeTrue("so must the process it started");

        // The interpreter state is gone with the process; the client says so instead of hanging.
        var later = () => sc.InvokeAsync("eval", Workers.Script("result = 1"), TimeSpan.FromSeconds(20)).WaitAsync(Workers.Deadline);
        (await later.Should().ThrowAsync<SidecarException>()).WithMessage("*timed out*");
    }
}

public sealed class SidecarClientTimeoutTests
{
    [PythonFact]
    public async Task Other_pending_calls_fail_too_when_one_call_times_out()
    {
        await using var sc = Workers.ScriptHost();

        var hung = sc.InvokeAsync("eval", Workers.Script("import time\ntime.sleep(600)"), TimeSpan.FromSeconds(1));
        var queued = sc.InvokeAsync("eval", Workers.Script("result = 1"), Timeout.InfiniteTimeSpan);

        await FluentActions.Awaiting(() => hung.WaitAsync(Workers.Deadline)).Should().ThrowAsync<SidecarException>();
        (await FluentActions.Awaiting(() => queued.WaitAsync(Workers.Deadline)).Should().ThrowAsync<SidecarException>())
            .WithMessage("*terminated because call 'eval' timed out*");
    }

    [PythonFact]
    public async Task The_clients_default_timeout_applies_when_the_call_names_none()
    {
        await using var sc = Workers.ScriptHost(callTimeout: TimeSpan.FromSeconds(1));

        var call = () => sc.InvokeAsync("eval", Workers.Script("while True: pass")).WaitAsync(Workers.Deadline);

        (await call.Should().ThrowAsync<SidecarException>()).Which.InnerException.Should().BeOfType<TimeoutException>();
    }

    [Fact]
    public void A_timeout_of_zero_or_less_is_rejected_before_anything_is_started()
    {
        var psi = new ProcessStartInfo { FileName = "this-is-never-started" };

        var act = () => new SidecarClient(psi, null, TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public sealed class SidecarClientInactivityTests
{
    [PythonFact]
    public async Task A_long_call_that_reports_progress_outlives_a_short_inactivity_timeout()
    {
        await using var sc = Workers.Noisy();
        // Let the interpreter finish starting first: its start-up is silence too, and on a busy
        // machine it can outlast the short timeout used below.
        await sc.InvokeAsync("echo", null).WaitAsync(Workers.Deadline);

        // Runs two and a half times as long as the timeout allows silence for.
        var result = await sc.InvokeAsync("slow_with_progress", new JsonObject { ["seconds"] = 2.5 }, TimeSpan.FromSeconds(1))
            .WaitAsync(Workers.Deadline);

        result!["ticks"]!.GetValue<int>().Should().BeGreaterThan(0);
    }

    [PythonFact]
    public async Task The_same_wait_without_progress_is_timed_out()
    {
        await using var sc = Workers.ScriptHost();

        var call = () => sc.InvokeAsync("eval", Workers.Script("import time\ntime.sleep(3)\nresult = 1"), TimeSpan.FromSeconds(1))
            .WaitAsync(Workers.Deadline);

        (await call.Should().ThrowAsync<SidecarException>()).Which.InnerException.Should().BeOfType<TimeoutException>();
    }
}

public sealed class SidecarClientCancellationTests
{
    [PythonFact]
    public async Task Cancellation_cancels_that_call_only_and_leaves_the_sidecar_running()
    {
        await using var sc = Workers.ScriptHost();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var cancelled = () => sc.InvokeAsync("eval", Workers.Script("import time\ntime.sleep(1)\nresult = 1"), cts.Token)
            .WaitAsync(Workers.Deadline);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        var next = await sc.InvokeAsync("eval", Workers.Script("result = 7")).WaitAsync(Workers.Deadline);
        next!["result"]!.GetValue<int>().Should().Be(7);
    }

    [PythonFact]
    public async Task A_call_with_an_already_cancelled_token_sends_nothing()
    {
        await using var sc = Workers.Echo();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var call = () => sc.InvokeAsync("ping", null, cts.Token);

        await call.Should().ThrowAsync<OperationCanceledException>();
        (await sc.InvokeAsync("ping", JsonValue.Create("ok")).WaitAsync(Workers.Deadline))!["pong"]!.GetValue<string>().Should().Be("ok");
    }
}

public sealed class SidecarClientDisposeTests
{
    [PythonFact]
    public async Task Dispose_ends_an_idle_sidecar_and_later_calls_are_refused()
    {
        var sc = Workers.ScriptHost();
        var pid = (await sc.InvokeAsync("eval", Workers.Script("import os\nresult = os.getpid()")).WaitAsync(Workers.Deadline))!["result"]!.GetValue<int>();
        Workers.IsRunning(pid).Should().BeTrue();

        await sc.DisposeAsync().AsTask().WaitAsync(Workers.Deadline);
        await sc.DisposeAsync(); // a second dispose is harmless

        (await Workers.ExitsWithin(pid, TimeSpan.FromSeconds(5))).Should().BeTrue();
        var later = () => sc.InvokeAsync("eval", Workers.Script("result = 1"));
        await later.Should().ThrowAsync<ObjectDisposedException>();
    }

    [PythonFact]
    public async Task Dispose_fails_a_call_that_is_still_pending_and_kills_the_busy_sidecar()
    {
        var sc = Workers.ScriptHost();
        var pid = (await sc.InvokeAsync("eval", Workers.Script("import os\nresult = os.getpid()")).WaitAsync(Workers.Deadline))!["result"]!.GetValue<int>();
        var pending = sc.InvokeAsync("eval", Workers.Script("import time\ntime.sleep(600)"), Timeout.InfiniteTimeSpan);

        await sc.DisposeAsync().AsTask().WaitAsync(Workers.Deadline);

        await FluentActions.Awaiting(() => pending.WaitAsync(Workers.Deadline)).Should().ThrowAsync<SidecarException>();
        (await Workers.ExitsWithin(pid, TimeSpan.FromSeconds(5))).Should().BeTrue("a sidecar that does not exit when asked is killed");
    }
}
