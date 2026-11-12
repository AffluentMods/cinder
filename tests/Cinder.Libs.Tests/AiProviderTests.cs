using System.Net;
using System.Text;
using Cinder.AI;
using Cinder.Artifacts;
using FluentAssertions;
using Xunit;

namespace Cinder.Libs.Tests;

/// <summary>
/// The AI providers as the app uses them: one long-lived <see cref="HttpClient"/> and a new
/// provider object for every Send or Test. Assigning <c>HttpClient.BaseAddress</c> in the
/// provider constructor threw from the second provider onwards, because the client had by then
/// sent a request — the copilot worked exactly once per session.
/// </summary>
public sealed class AiProviderTests
{
    private sealed class RecordingHandler(Func<HttpRequestMessage, (string Body, string ContentType)> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString()));
            var (body, contentType) = respond(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            });
        }
    }

    private static RecordingHandler ChatHandler() => new(request =>
        request.RequestUri!.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal)
            ? ("""{"message":{"role":"assistant","content":"from ollama"},"done":true}""", "application/json")
            : request.RequestUri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? ("""{"choices":[{"message":{"role":"assistant","content":"from openai"}}]}""", "application/json")
                : ("{}", "application/json"));

    private static readonly AiPrompt Prompt = new("system", [new AiMessage("user", "hello")], new AiPromptOptions());

    [Fact]
    public async Task A_new_provider_can_be_built_on_a_client_that_has_already_sent_requests()
    {
        var handler = ChatHandler();
        using var http = new HttpClient(handler);

        // First Send.
        (await new OllamaProvider(http, "llama3", new Uri("http://localhost:11434")).CompleteAsync(Prompt, CancellationToken.None))
            .Should().Be("from ollama");

        // Second Send: the app builds another provider over the same, now-used, client.
        var second = () => new OllamaProvider(http, "llama3", new Uri("http://localhost:11434"));
        second.Should().NotThrow("HttpClient.BaseAddress cannot be set once a request has been sent");
        (await second().CompleteAsync(Prompt, CancellationToken.None)).Should().Be("from ollama");

        // …and the user switches provider in Settings without restarting.
        var openAi = () => new OpenAiCompatibleProvider(http, "openai", "OpenAI", new Uri("https://api.example.com/v1/"), "gpt", () => "sk-test");
        openAi.Should().NotThrow();
        (await openAi().CompleteAsync(Prompt, CancellationToken.None)).Should().Be("from openai");
        (await new LmStudioProvider(http, "local", new Uri("http://localhost:1234/v1/")).HealthCheckAsync(CancellationToken.None)).Should().BeTrue();

        http.BaseAddress.Should().BeNull("the provider must not reconfigure a client it does not own");
        handler.Requests.Select(r => r.Uri.ToString()).Should().Equal(
            "http://localhost:11434/api/chat",
            "http://localhost:11434/api/chat",
            "https://api.example.com/v1/chat/completions",
            "http://localhost:1234/v1/models");
    }

    [Fact]
    public async Task Two_providers_on_one_client_each_talk_to_their_own_endpoint()
    {
        var handler = ChatHandler();
        using var http = new HttpClient(handler);
        var local = new OllamaProvider(http, "llama3", new Uri("http://localhost:11434"));
        var remote = new OpenAiCompatibleProvider(http, "openai", "OpenAI", new Uri("https://api.example.com/v1"), "gpt", () => "sk-test");

        await remote.CompleteAsync(Prompt, CancellationToken.None);
        await local.CompleteAsync(Prompt, CancellationToken.None);
        await local.HealthCheckAsync(CancellationToken.None);

        handler.Requests[0].Uri.Host.Should().Be("api.example.com");
        handler.Requests[0].Authorization.Should().Be("Bearer sk-test");
        handler.Requests[1].Uri.Should().Be(new Uri("http://localhost:11434/api/chat"));
        handler.Requests[1].Authorization.Should().BeNull("the API key for one provider must never ride along to another");
        handler.Requests[2].Uri.Should().Be(new Uri("http://localhost:11434/api/tags"));
    }

    [Theory]
    [InlineData("https://api.example.com/v1", "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/v1/", "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/v1/chat/completions", "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/v1/?api-version=1#frag", "https://api.example.com/v1/chat/completions")]
    [InlineData("http://gateway.internal:8000", "http://gateway.internal:8000/chat/completions")]
    [InlineData("http://gateway.internal:8000/openai/deployments/x", "http://gateway.internal:8000/openai/deployments/x/chat/completions")]
    public async Task The_endpoint_is_normalised_however_it_was_typed(string typed, string expectedRequest)
    {
        var handler = ChatHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(http, "openai", "OpenAI", new Uri(typed), "gpt", () => null);

        await provider.CompleteAsync(Prompt, CancellationToken.None);

        handler.Requests.Single().Uri.ToString().Should().Be(expectedRequest);
        provider.Endpoint.AbsolutePath.Should().EndWith("/");
    }

    [Fact]
    public async Task Streaming_uses_the_same_endpoint_resolution()
    {
        var handler = new RecordingHandler(_ => (
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\ndata: [DONE]\n\n",
            "text/event-stream"));
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleProvider(http, "openai", "OpenAI", new Uri("https://api.example.com/v1"), "gpt", () => null);

        var text = new StringBuilder();
        await foreach (var chunk in provider.StreamCompletionAsync(Prompt, CancellationToken.None))
        {
            text.Append(chunk);
        }

        text.ToString().Should().Be("Hello");
        handler.Requests.Single().Uri.ToString().Should().Be("https://api.example.com/v1/chat/completions");
    }

    [Theory]
    [InlineData("ftp://example.com/v1")]
    [InlineData("file:///C:/models")]
    public void A_non_http_endpoint_is_rejected(string endpoint)
    {
        var act = () => AiEndpoint.Normalize(new Uri(endpoint));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Local_only_reflects_where_the_endpoint_actually_is()
    {
        using var http = new HttpClient(ChatHandler());

        new OllamaProvider(http).Capabilities.LocalOnly.Should().BeTrue();
        new OllamaProvider(http, endpoint: new Uri("http://[::1]:11434")).Capabilities.LocalOnly.Should().BeTrue();
        new OllamaProvider(http, endpoint: new Uri("http://10.0.0.5:11434")).Capabilities.LocalOnly
            .Should().BeFalse("prompts carry evidence; a LAN host is not 'this machine'");
        new LmStudioProvider(http).Capabilities.LocalOnly.Should().BeTrue();
        new LmStudioProvider(http, endpoint: new Uri("https://lmstudio.example.com/v1/")).Capabilities.LocalOnly.Should().BeFalse();
        new OpenAiCompatibleProvider(http, "x", "x", new Uri("http://127.0.0.1:8000/v1"), "m", () => null).Capabilities.LocalOnly.Should().BeTrue();
        new OpenAiCompatibleProvider(http, "x", "x", new Uri("https://api.example.com/v1"), "m", () => null).Capabilities.LocalOnly.Should().BeFalse();
    }

    // ---- NaturalLanguageQuery ------------------------------------------------------------------

    private sealed class CannedProvider(string reply) : IAiProvider
    {
        public string Id => "canned";
        public string DisplayName => "canned";
        public AiProviderCapabilities Capabilities { get; } = new(false, false, false, 0, true);
        public Task<bool> HealthCheckAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<string> CompleteAsync(AiPrompt prompt, CancellationToken ct) => Task.FromResult(reply);
        public async IAsyncEnumerable<string> StreamCompletionAsync(AiPrompt prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return reply;
        }
    }

    [Fact]
    public async Task The_snake_case_fields_the_model_is_asked_for_are_bound()
    {
        // The schema in the system prompt is snake_case. FromUtc/ToUtc/TextContains used to stay
        // null, so "USB activity for alice in January" ran as "everything for alice".
        const string Reply = """
            Sure! Here is the query:
            {"kind":"timeline","user":"alice","from_utc":"2026-01-01T00:00:00Z","to_utc":"2026-02-01T00:00:00+01:00",
             "sources":["evtx","registry.usbstor"],"text_contains":"usb","limit":50}
            """;

        var query = await new NaturalLanguageQuery(new CannedProvider(Reply)).TranslateAsync("usb activity for alice in January");

        query.Should().NotBeNull();
        query!.Kind.Should().Be("timeline");
        query.User.Should().Be("alice");
        query.TextContains.Should().Be("usb");
        query.Sources.Should().Equal("evtx", "registry.usbstor");
        query.Limit.Should().Be(50);
        DateTimeOffset.Parse(query.FromUtc!).Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        DateTimeOffset.Parse(query.ToUtc!).Should().Be(new DateTimeOffset(2026, 1, 31, 23, 0, 0, TimeSpan.Zero));
        query.ToUtc.Should().EndWith("+00:00", "dates are normalised to UTC");
    }

    [Theory]
    [InlineData("""{"kind":"drop_tables","limit":5}""", "an unknown kind")]
    [InlineData("""{"limit":5}""", "no kind at all")]
    [InlineData("""{"kind":"timeline","from_utc":"last tuesday"}""", "a date that cannot be parsed")]
    [InlineData("""{"kind":"timeline","from_utc":"2026-02-01T00:00:00Z","to_utc":"2026-01-01T00:00:00Z"}""", "an inverted range")]
    [InlineData("""{"kind":"timeline","limit":"many"}""", "a mistyped field")]
    [InlineData("""{"kind":"timeline",""", "truncated JSON")]
    public void A_query_that_does_not_validate_is_not_run_with_a_filter_missing(string json, string because)
    {
        NaturalLanguageQuery.Parse(json).Should().BeNull(because);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1000, 1000)]
    [InlineData(1_000_000, 1000)]
    public void Limit_is_clamped_to_the_schema_range(int requested, int expected)
    {
        NaturalLanguageQuery.Parse($$"""{"kind":"SEARCH","limit":{{requested}}}""")!.Limit.Should().Be(expected);
        NaturalLanguageQuery.Parse("""{"kind":"SEARCH"}""")!.Kind.Should().Be("search");
        NaturalLanguageQuery.Parse("""{"kind":"search"}""")!.Limit.Should().BeNull();
    }

    // ---- AnomalyDetector ------------------------------------------------------------------------

    private sealed record Event(string Source, string? User, DateTimeOffset? Timestamp, string Summary)
        : ArtifactBase(Source, User, Timestamp, Summary);

    [Fact]
    public void An_activity_burst_is_reported_as_a_utc_day_whatever_zone_the_workstation_is_in()
    {
        // Four quiet UTC days and one with 40 events. The events of the busy day carry a +10:00
        // offset and straddle local midnight, to show the day is the UTC day, not the offset's.
        var events = new List<IArtifact>();
        for (int day = 1; day <= 4; day++)
        {
            for (int i = 0; i < 5; i++)
            {
                events.Add(new Event("evtx", "alice", new DateTimeOffset(2026, 3, day, 10, i, 0, TimeSpan.Zero), "quiet"));
            }
        }
        var busyUtcMorning = new DateTimeOffset(2026, 3, 10, 1, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 40; i++)
        {
            // 11:00–11:39 local at +10:00 == 01:00–01:39 UTC on the 10th.
            events.Add(new Event("evtx", "alice", busyUtcMorning.AddMinutes(i).ToOffset(TimeSpan.FromHours(10)), "burst"));
        }

        var burst = new AnomalyDetector().Detect(events).Single(a => a.Kind == "activity-burst");

        burst.From.Should().Be(new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero));
        burst.To.Should().Be(new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero));
        burst.From.Offset.Should().Be(TimeSpan.Zero, "the window must not pick up the examiner's local UTC offset");
        burst.Message.Should().Contain("2026-03-10").And.Contain("40 events");
    }

    [Fact]
    public void A_burst_late_in_the_utc_day_is_not_moved_to_the_next_day_by_the_events_own_offset()
    {
        // 30 events at 23:30 UTC on the 5th, recorded with a +14:00 offset — 13:30 on the 6th
        // by that clock. Hours are judged in UTC, so days must be too: grouping by the offset's
        // calendar date put this burst on the 6th, a day on which (in UTC) nothing happened.
        var events = new List<IArtifact>();
        foreach (var day in new[] { 1, 2, 3, 4, 8, 9 })
        {
            events.Add(new Event("evtx", "bob", new DateTimeOffset(2026, 4, day, 12, 0, 0, TimeSpan.Zero), "quiet"));
        }
        for (int i = 0; i < 30; i++)
        {
            events.Add(new Event("evtx", "bob", new DateTimeOffset(2026, 4, 5, 23, 30, i, TimeSpan.Zero).ToOffset(TimeSpan.FromHours(14)), "late"));
        }

        var burst = new AnomalyDetector().Detect(events).Single(a => a.Kind == "activity-burst");

        burst.From.Should().Be(new DateTimeOffset(2026, 4, 5, 0, 0, 0, TimeSpan.Zero));
        burst.To.Should().Be(new DateTimeOffset(2026, 4, 6, 0, 0, 0, TimeSpan.Zero));
        burst.Message.Should().Contain("2026-04-05");
    }
}
