using System.Text.Json.Nodes;

namespace Cinder.Sidecar.Tests;

/// <summary>
/// An <see cref="ISidecarChannel"/> that answers from canned JSON, so an adapter's request
/// building and response mapping can be tested without a worker process. It records every
/// request it was sent.
/// </summary>
internal sealed class FakeSidecarChannel : ISidecarChannel
{
    private readonly Func<FakeCall, JsonNode?> _answer;

    public FakeSidecarChannel(Func<FakeCall, JsonNode?> answer) => _answer = answer;

    /// <summary>Every request sent, in order.</summary>
    public List<FakeCall> Calls { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>Answers every request with the same reply.</summary>
    public static FakeSidecarChannel Returning(string json) => new(_ => JsonNode.Parse(json));

    /// <summary>Answers the n-th request with the n-th reply.</summary>
    public static FakeSidecarChannel Sequence(params string[] replies)
    {
        var index = 0;
        return new FakeSidecarChannel(call =>
        {
            if (index >= replies.Length)
            {
                throw new InvalidOperationException($"Unexpected extra request #{index + 1}: {call.Method} {call.Params.ToJsonString()}");
            }
            return JsonNode.Parse(replies[index++]);
        });
    }

    public Task<JsonNode?> InvokeAsync(string method, JsonNode? @params, IProgress<JsonObject>? progress,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ct.ThrowIfCancellationRequested();
        // Round-trip through text: the adapter must send plain JSON, and the copy keeps the
        // recorded request independent of whatever the adapter does to its object afterwards.
        var sent = @params is null ? new JsonObject() : JsonNode.Parse(@params.ToJsonString())!.AsObject();
        var call = new FakeCall(method, sent, progress, timeout);
        Calls.Add(call);
        return Task.FromResult(_answer(call));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>One request as the adapter sent it.</summary>
internal sealed record FakeCall(string Method, JsonObject Params, IProgress<JsonObject>? Progress, TimeSpan? Timeout)
{
    /// <summary>Emits a <c>$/progress</c> notification for this call, as the client would deliver it.</summary>
    public void ReportProgress(string json) => Progress?.Report(JsonNode.Parse(json)!.AsObject());
}

/// <summary>Hands out channels and remembers them, so a test can see how many were opened.</summary>
internal sealed class FakeChannelFactory
{
    private readonly Func<FakeSidecarChannel> _create;

    public FakeChannelFactory(Func<FakeSidecarChannel> create) => _create = create;

    /// <summary>Every channel opened answers every request with the same reply.</summary>
    public static FakeChannelFactory Returning(string json) => new(() => FakeSidecarChannel.Returning(json));

    public List<FakeSidecarChannel> Opened { get; } = [];

    /// <summary>All requests across every channel opened, in order.</summary>
    public IEnumerable<FakeCall> Calls => Opened.SelectMany(c => c.Calls);

    public ISidecarChannel Open()
    {
        var channel = _create();
        Opened.Add(channel);
        return channel;
    }
}

/// <summary>An <see cref="IProgress{T}"/> that records synchronously (unlike <see cref="Progress{T}"/>).</summary>
internal sealed class RecordingProgress<T> : IProgress<T>
{
    public List<T> Reports { get; } = [];

    public void Report(T value)
    {
        lock (Reports)
        {
            Reports.Add(value);
        }
    }
}
