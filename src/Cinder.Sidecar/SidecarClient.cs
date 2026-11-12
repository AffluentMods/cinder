using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Sidecar;

/// <summary>
/// Spawns a sidecar process and exchanges JSON-RPC messages over its stdio. NDJSON framing —
/// one request or response per line, UTF-8 without a BOM in both directions.
/// <para>
/// Thread-safe: many callers can <see cref="InvokeAsync(string, JsonNode, CancellationToken)"/>
/// concurrently. Requests are written one whole line at a time under a lock, and responses fan
/// out by id.
/// </para>
/// <para>
/// A call never waits forever. It fails with a <see cref="SidecarException"/> when the sidecar
/// exits, when the sidecar reports an error it could not attribute to a request (a line it
/// could not parse: the request that line carried is lost, so every pending call is failed),
/// or when the call's timeout expires. The timeout is an <em>inactivity</em> timeout: it runs
/// out when the sidecar has sent nothing at all — no response to any call and no
/// <c>$/progress</c> notification — for that long while the call is pending. A long job that
/// keeps reporting progress is left alone; a hung or runaway one is not. When it expires the
/// whole sidecar process tree is killed, because a worker that has stopped answering cannot be
/// trusted with the next request either, and the client must then be disposed.
/// </para>
/// <para>
/// Everything a sidecar runs (parsers, and user scripts in the scripting host) executes with
/// the rights of this process. The sidecar is not a sandbox.
/// </para>
/// </summary>
public sealed class SidecarClient : IAsyncDisposable
{
    /// <summary>Inactivity timeout applied to calls that do not specify their own.</summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Method name of the sidecar's "still working" notification.</summary>
    public const string ProgressMethod = "$/progress";

    private const int MaxStderrLineChars = 8192;
    private const int StderrTailLines = 20;
    private static readonly TimeSpan MaxTimerSlice = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<Inbound>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Queue<string> _stderrTail = new();
    private readonly Task _readLoop;
    private readonly Task _stderrPump;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _callTimeout;
    private long _nextId;
    private long _lastActivity = Stopwatch.GetTimestamp();
    private volatile string? _terminalFailure;
    private volatile bool _disposed;

    /// <param name="psi">How to start the sidecar. Stdio redirection, UTF-8 encodings and
    /// <c>PYTHONIOENCODING</c> are set here; callers need not (and cannot) choose them.</param>
    /// <param name="logger">Receives the sidecar's stderr at Debug level.</param>
    /// <param name="callTimeout">Inactivity timeout for calls that do not pass their own;
    /// <see cref="DefaultCallTimeout"/> when null, <see cref="Timeout.InfiniteTimeSpan"/> to disable.</param>
    public SidecarClient(ProcessStartInfo psi, ILogger? logger = null, TimeSpan? callTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(psi);
        _callTimeout = ValidateTimeout(callTimeout ?? DefaultCallTimeout, nameof(callTimeout));

        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        // The protocol is UTF-8 on every platform. Left alone, .NET decodes the pipes with the
        // console code page and Python encodes a piped stdout with the ANSI code page (cp1252
        // on most Windows installs): any CJK / Cyrillic / emoji string then fails the call and
        // accented Latin text arrives corrupted. No BOM: it would be the first bytes of the
        // first request.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        psi.StandardInputEncoding = utf8;
        psi.StandardOutputEncoding = utf8;
        psi.StandardErrorEncoding = utf8;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        _logger = logger ?? NullLogger.Instance;
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!_process.Start())
        {
            throw new SidecarException($"Failed to start sidecar: {psi.FileName}");
        }

        _stderrPump = Task.Run(StderrPumpAsync);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Invokes <paramref name="method"/> with this client's default inactivity timeout.</summary>
    public Task<JsonNode?> InvokeAsync(string method, JsonNode? @params, CancellationToken ct = default)
        => InvokeAsync(method, @params, _callTimeout, ct);

    /// <summary>
    /// Invokes <paramref name="method"/>. Fails with a <see cref="SidecarException"/> (inner
    /// <see cref="TimeoutException"/>) and kills the sidecar process tree if the sidecar sends
    /// nothing for <paramref name="timeout"/> while the call is pending. Pass
    /// <see cref="Timeout.InfiniteTimeSpan"/> to wait without limit.
    /// </summary>
    public async Task<JsonNode?> InvokeAsync(string method, JsonNode? @params, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ValidateTimeout(timeout, nameof(timeout));
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<Inbound>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        // Checked after registering: if the read loop ended before this point nobody is left to
        // complete the call, and if it ends after, FailAllPending will find it.
        if (_terminalFailure is { } failure)
        {
            _pending.TryRemove(id, out _);
            throw new SidecarException(failure);
        }

        var request = new JsonRpcRequest { Id = id, Method = method, Params = @params };
        var line = JsonSerializer.Serialize(request, JsonOptions) + "\n";

        using var watchdogCts = new CancellationTokenSource();
        var watchdog = timeout == Timeout.InfiniteTimeSpan
            ? Task.CompletedTask
            : WatchAsync(method, tcs, timeout, watchdogCts.Token);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        try
        {
            await SendAsync(line, tcs, ct).ConfigureAwait(false);
            var response = await tcs.Task.ConfigureAwait(false);
            if (response.Error is { } err)
            {
                throw new SidecarException(err.Code, err.Message);
            }
            return response.Result;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            await watchdogCts.CancelAsync().ConfigureAwait(false);
            await watchdog.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes one request line. The semaphore makes "write the whole line, then flush" atomic
    /// across callers: two interleaved requests are two unparseable lines, each answered by an
    /// error with no id that no caller could ever be matched to.
    /// </summary>
    private async Task SendAsync(string line, TaskCompletionSource<Inbound> tcs, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Not cancellable once started: half a line would corrupt the stream for everyone.
            var stdin = _process.StandardInput;
            await stdin.WriteAsync(line.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            await stdin.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (tcs.Task.IsFaulted)
            {
                // The call was already failed for a better reason (timeout, sidecar exit) and
                // the broken pipe is only its consequence.
                await tcs.Task.ConfigureAwait(false);
            }
            throw new SidecarException(_terminalFailure ?? "Failed to write to sidecar stdin.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WatchAsync(string method, TaskCompletionSource<Inbound> tcs, TimeSpan timeout, CancellationToken stop)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (!tcs.Task.IsCompleted)
            {
                var since = Math.Max(started, Interlocked.Read(ref _lastActivity));
                var idle = Stopwatch.GetElapsedTime(since);
                if (idle >= timeout)
                {
                    OnTimeout(method, timeout, tcs);
                    return;
                }
                var remaining = timeout - idle;
                await Task.Delay(remaining < MaxTimerSlice ? remaining : MaxTimerSlice, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The call completed.
        }
    }

    private void OnTimeout(string method, TimeSpan timeout, TaskCompletionSource<Inbound> tcs)
    {
        var message =
            $"Sidecar call '{method}' timed out: the sidecar sent no response and no progress for {timeout}. " +
            "The sidecar process was terminated.";
        if (!tcs.TrySetException(new SidecarException(message, new TimeoutException(message))))
        {
            return; // completed in the meantime
        }
        _logger.LogWarning("{Message}", message);
        _terminalFailure ??= $"Sidecar process was terminated because call '{method}' timed out after {timeout}.";
        KillProcessTree();
        FailAllPending(_terminalFailure);
    }

    private void KillProcessTree()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Killing the sidecar process tree failed.");
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                if (line.Length == 0)
                {
                    continue;
                }

                Inbound? message;
                try
                {
                    message = JsonSerializer.Deserialize<Inbound>(line, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Sidecar emitted unparseable line: {Line}", Truncate(line));
                    continue;
                }
                if (message is null)
                {
                    continue;
                }

                // Anything well-formed from the sidecar is a sign of life for the watchdogs.
                Interlocked.Exchange(ref _lastActivity, Stopwatch.GetTimestamp());

                if (message.Id is { } id)
                {
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        tcs.TrySetResult(message);
                    }
                    continue;
                }

                if (message.Error is { } error)
                {
                    // An error with no id: the sidecar received a line it could not read as a
                    // request, so it cannot say which call it belonged to. That call would
                    // otherwise wait forever; fail everything that is pending.
                    _logger.LogError("Sidecar reported a protocol error with no request id: {Message}", error.Message);
                    FailAllPending($"Sidecar could not read a request ({error.Code}: {error.Message}). " +
                                   "The affected call cannot be identified, so all pending calls were failed.");
                    continue;
                }

                if (!string.Equals(message.Method, ProgressMethod, StringComparison.Ordinal))
                {
                    _logger.LogDebug("Sidecar sent an unexpected message without an id: {Line}", Truncate(line));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sidecar read loop crashed.");
        }
        finally
        {
            // Give the stderr pump a moment to deliver the traceback that explains the exit.
            await Task.WhenAny(_stderrPump, Task.Delay(250)).ConfigureAwait(false);
            _terminalFailure ??= DescribeExit();
            FailAllPending(_terminalFailure);
        }
    }

    private string DescribeExit()
    {
        var sb = new StringBuilder("Sidecar process ended");
        try
        {
            if (_process.WaitForExit(500))
            {
                sb.Append(" (exit code ").Append(_process.ExitCode).Append(')');
            }
        }
        catch (Exception)
        {
            // Exit code unavailable; the message still says what matters.
        }
        sb.Append('.');
        string[] tail;
        lock (_stderrTail)
        {
            tail = [.. _stderrTail];
        }
        if (tail.Length > 0)
        {
            sb.Append(" Last stderr output: ").AppendJoin(" | ", tail);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Drains stderr until the process closes it or the client is disposed. This must never stop early: with
    /// protocol frames on stdout, everything else the sidecar prints (logs, tracebacks, a user
    /// script's output) goes to stderr, and a full stderr pipe blocks the sidecar on its next
    /// write. Lines are length-capped so that output without newlines cannot grow without bound.
    /// </summary>
    private async Task StderrPumpAsync()
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var overflowed = false;
        try
        {
            var stderr = _process.StandardError;
            while (true)
            {
                var read = await stderr.ReadAsync(buffer.AsMemory(), _cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c == '\n')
                    {
                        EmitStderrLine(line, overflowed);
                        line.Clear();
                        overflowed = false;
                    }
                    else if (line.Length < MaxStderrLineChars)
                    {
                        line.Append(c);
                    }
                    else
                    {
                        overflowed = true;
                    }
                }
            }
            if (line.Length > 0)
            {
                EmitStderrLine(line, overflowed);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // The pipe closed underneath us: the process is gone or the client was disposed.
        }
    }

    private void EmitStderrLine(StringBuilder buffer, bool overflowed)
    {
        var text = buffer.ToString().TrimEnd('\r');
        if (text.Length == 0)
        {
            return;
        }
        if (overflowed)
        {
            text += " [line truncated]";
        }
        lock (_stderrTail)
        {
            _stderrTail.Enqueue(text.Length > 400 ? text[..400] + "…" : text);
            while (_stderrTail.Count > StderrTailLines)
            {
                _stderrTail.Dequeue();
            }
        }
        try
        {
            _logger.LogDebug("[sidecar] {Line}", text);
        }
        catch (Exception)
        {
            // A failing log sink must not stop the drain.
        }
    }

    private void FailAllPending(string message)
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetException(new SidecarException(message));
            }
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string paramName)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, timeout, "Timeout must be positive or Timeout.InfiniteTimeSpan.");
        }
        return timeout;
    }

    private static string Truncate(string line) => line.Length <= 500 ? line : line[..500] + "…";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _terminalFailure ??= "Sidecar disposed.";

        try
        {
            if (!_process.HasExited)
            {
                // Closing stdin is the polite request to exit: the sidecar's read loop sees EOF.
                _process.StandardInput.Close();
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    KillProcessTree();
                }
            }
        }
        catch
        {
            // Best effort.
            KillProcessTree();
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
            // Bounded: a grandchild that inherited stderr could hold the pipe open.
            await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // Best effort.
        }

        FailAllPending("Sidecar disposed.");
        _cts.Dispose();
        _process.Dispose();
    }

    /// <summary>Any line the sidecar can send: a response (id), a protocol error (no id, error)
    /// or a notification (no id, method).</summary>
    private sealed class Inbound
    {
        [JsonPropertyName("id")] public long? Id { get; init; }
        [JsonPropertyName("result")] public JsonNode? Result { get; init; }
        [JsonPropertyName("error")] public JsonRpcError? Error { get; init; }
        [JsonPropertyName("method")] public string? Method { get; init; }
    }
}
