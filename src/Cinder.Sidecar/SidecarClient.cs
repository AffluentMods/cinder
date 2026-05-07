using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Sidecar;

/// <summary>
/// Spawns a sidecar process and exchanges JSON-RPC messages over its stdio. NDJSON framing —
/// one request or response per line. Thread-safe: many callers can <see cref="InvokeAsync"/>
/// concurrently and responses fan out by id.
/// </summary>
public sealed class SidecarClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonRpcResponse>> _pending = new();
    private readonly Task _readLoop;
    private readonly CancellationTokenSource _cts = new();
    private long _nextId;
    private bool _disposed;

    public SidecarClient(ProcessStartInfo psi, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(psi);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        _logger = logger ?? NullLogger.Instance;
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!_process.Start())
        {
            throw new SidecarException($"Failed to start sidecar: {psi.FileName}");
        }

        _readLoop = Task.Run(ReadLoopAsync);
        _ = Task.Run(StderrPumpAsync);
    }

    public async Task<JsonNode?> InvokeAsync(string method, JsonNode? @params, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new JsonRpcRequest { Id = id, Method = method, Params = @params };
        var json = JsonSerializer.Serialize(request, JsonOptions);

        try
        {
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            throw new SidecarException("Failed to write to sidecar stdin.", ex);
        }

        await using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var t))
            {
                t.TrySetCanceled(ct);
            }
        });

        var response = await tcs.Task.ConfigureAwait(false);
        if (response.Error is { } err)
        {
            throw new SidecarException(err.Code, err.Message);
        }
        return response.Result;
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

                JsonRpcResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<JsonRpcResponse>(line, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Sidecar emitted unparseable line: {Line}", line);
                    continue;
                }

                if (response?.Id is not { } id)
                {
                    continue;
                }
                if (_pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(response);
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
            FailAllPending(new SidecarException("Sidecar process ended."));
        }
    }

    private async Task StderrPumpAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                if (line.Length > 0)
                {
                    _logger.LogDebug("[sidecar] {Line}", line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sidecar stderr pump ended.");
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var (_, tcs) in _pending)
        {
            tcs.TrySetException(ex);
        }
        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(2000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            // Best effort.
        }

        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch
        {
            // Best effort.
        }

        FailAllPending(new SidecarException("Sidecar disposed."));
        _cts.Dispose();
        _process.Dispose();
    }
}
