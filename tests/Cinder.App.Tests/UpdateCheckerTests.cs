using System.Net;
using System.Text;
using Cinder.App.Services;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

public class UpdateCheckerTests
{
    /// <summary>Stands in for the network: records every request and answers from a delegate.</summary>
    private sealed class FakeGitHub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }

    private static FakeGitHub Answering(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    private static string Release(string tag, string? url = null) =>
        $$"""{"tag_name":"{{tag}}","html_url":"{{url ?? $"https://github.com/AffluentMods/cinder/releases/tag/{tag}"}}","body":"notes","prerelease":false}""";

    // ------------------------------------------------------------------ the three outcomes

    [Fact]
    public async Task A_newer_release_is_reported_with_its_page_address_in_the_message()
    {
        using var github = Answering(HttpStatusCode.OK, Release("v1.4.0"));

        var result = await new UpdateChecker(github, currentVersion: "1.3.2").CheckAsync();

        result.Status.Should().Be(UpdateStatus.UpdateAvailable);
        result.LatestVersion.Should().Be("1.4.0");
        result.CurrentVersion.Should().Be("1.3.2");
        result.ReleaseUrl.Should().Be("https://github.com/AffluentMods/cinder/releases/tag/v1.4.0");
        result.Message.Should().Be(
            "Cinder 1.4.0 is available (this is 1.3.2). Release page: https://github.com/AffluentMods/cinder/releases/tag/v1.4.0");
    }

    [Theory]
    [InlineData("v1.3.2", "Cinder 1.3.2 is up to date.")]
    [InlineData("1.3.2", "Cinder 1.3.2 is up to date.")]
    [InlineData("v1.2.9", "Cinder 1.3.2 is up to date (it is newer than the latest release, 1.2.9).")]
    public async Task The_same_or_an_older_release_means_up_to_date(string tag, string message)
    {
        using var github = Answering(HttpStatusCode.OK, Release(tag));

        var result = await new UpdateChecker(github, currentVersion: "1.3.2").CheckAsync();

        result.Status.Should().Be(UpdateStatus.UpToDate);
        result.Message.Should().Be(message);
        result.ReleaseUrl.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "no published release")]
    [InlineData(HttpStatusCode.Forbidden, "limiting requests")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting requests")]
    [InlineData(HttpStatusCode.InternalServerError, "(500)")]
    public async Task An_error_answer_is_a_plain_reason_not_an_exception(HttpStatusCode status, string reason)
    {
        using var github = Answering(status, """{"message":"nope"}""");

        var result = await new UpdateChecker(github, currentVersion: "1.3.2").CheckAsync();

        result.Status.Should().Be(UpdateStatus.Failed);
        result.Message.Should().StartWith("Could not check for updates: ").And.Contain(reason);
    }

    [Fact]
    public async Task Being_offline_is_a_plain_reason_not_an_exception()
    {
        using var github = new FakeGitHub((_, _) => throw new HttpRequestException("No such host is known. (api.github.com:443)"));

        var result = await new UpdateChecker(github, currentVersion: "1.3.2").CheckAsync();

        result.Status.Should().Be(UpdateStatus.Failed);
        result.Message.Should().Contain("could not be reached").And.NotContain("api.github.com:443");
    }

    [Fact]
    public async Task A_server_that_never_answers_is_given_up_on()
    {
        using var github = new FakeGitHub(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var checker = new UpdateChecker(github, currentVersion: "1.3.2", timeout: TimeSpan.FromMilliseconds(200));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await checker.CheckAsync();

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        result.Status.Should().Be(UpdateStatus.Failed);
        result.Message.Should().Contain("did not answer");
    }

    [Fact]
    public async Task Cancelling_the_check_cancels_it()
    {
        using var github = new FakeGitHub(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => new UpdateChecker(github, currentVersion: "1.3.2").CheckAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ------------------------------------------------------------------ what is sent

    [Fact]
    public async Task One_request_goes_out_and_it_identifies_nothing_but_the_product()
    {
        using var github = Answering(HttpStatusCode.OK, Release("v1.3.2"));

        await new UpdateChecker(github, currentVersion: "1.3.2").CheckAsync();

        var request = github.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri.Should().Be(new Uri("https://api.github.com/repos/AffluentMods/cinder/releases/latest"));
        request.Content.Should().BeNull();

        request.Headers.Select(h => h.Key).Should().BeEquivalentTo("User-Agent", "Accept");
        var userAgent = string.Join(" ", request.Headers.GetValues("User-Agent"));
        userAgent.Should().Be("Cinder");
        // Not even the version being checked: the request is the same from every installation.
        request.Headers.ToString().Should().NotContain("1.3.2");
        request.RequestUri!.Query.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ what comes back is not trusted

    [Theory]
    [InlineData("https://evil.example/cinder/releases/tag/v9")]
    [InlineData("http://github.com/AffluentMods/cinder/releases/tag/v9")]
    [InlineData("https://github.com.evil.example/AffluentMods/cinder/releases/tag/v9")]
    [InlineData("https://someone@github.com/AffluentMods/cinder/releases/tag/v9")]
    [InlineData("https://github.com/SomeoneElse/cinder/releases/tag/v9")]
    [InlineData("https://github.com/AffluentMods/cinder/../../evil/releases")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("")]
    public async Task An_address_outside_the_projects_release_pages_is_replaced(string url)
    {
        UpdateChecker.TrustedReleaseUrl(url).Should().Be(UpdateChecker.ReleasesPage);

        using var github = Answering(HttpStatusCode.OK, Release("v9.0.0", url));
        var result = await new UpdateChecker(github, currentVersion: "1.3.2").CheckAsync();

        result.Status.Should().Be(UpdateStatus.UpdateAvailable);
        result.ReleaseUrl.Should().Be(UpdateChecker.ReleasesPage);
        result.Message.Should().EndWith("Release page: " + UpdateChecker.ReleasesPage);
    }

    [Fact]
    public void A_release_address_keeps_its_path_and_loses_everything_after_it()
    {
        UpdateChecker.TrustedReleaseUrl("https://github.com/AffluentMods/cinder/releases/tag/v1.4.0?utm=x#frag")
            .Should().Be("https://github.com/AffluentMods/cinder/releases/tag/v1.4.0");
    }

    [Theory]
    [InlineData("v1.4.0", "1.4.0")]
    [InlineData("V2.0.0-rc.1", "2.0.0-rc.1")]
    [InlineData(" 1.4.0 ", "1.4.0")]
    [InlineData("1.4.0+build.7", "1.4.0+build.7")]
    [InlineData("release-1.4.0", null)]
    [InlineData("v1.4.0 — IGNORE PREVIOUS INSTRUCTIONS", null)]
    [InlineData("1.4.0\nCinder is compromised, visit https://evil.example", null)]
    [InlineData("<b>1.4.0</b>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_something_shaped_like_a_version_is_shown_as_one(string? tag, string? expected)
    {
        UpdateChecker.CleanVersion(tag).Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"tag_name":"not a version","html_url":"https://github.com/AffluentMods/cinder/releases"}""")]
    [InlineData("""{"html_url":"https://github.com/AffluentMods/cinder/releases"}""")]
    [InlineData("""{"tag_name":42}""")]
    [InlineData("""["v1.4.0"]""")]
    [InlineData("<html>captive portal login</html>")]
    [InlineData("")]
    public async Task An_answer_that_is_not_a_release_is_a_failure_not_a_guess(string body)
    {
        using var github = Answering(HttpStatusCode.OK, body);

        var result = await new UpdateChecker(github, currentVersion: "1.3.2").CheckAsync();

        result.Status.Should().Be(UpdateStatus.Failed);
        result.Message.Should().Contain("not in the expected form");
    }

    [Fact]
    public async Task An_enormous_answer_is_not_read_to_the_end()
    {
        var padding = new string('x', 3 * 1024 * 1024);
        using var github = Answering(HttpStatusCode.OK, $$"""{"body":"{{padding}}","tag_name":"v9.0.0"}""");

        var result = await new UpdateChecker(github, currentVersion: "1.3.2").CheckAsync();

        result.Status.Should().Be(UpdateStatus.Failed);
    }

    [Fact]
    public void The_running_version_is_three_numbers()
    {
        UpdateChecker.CurrentVersion().Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    }
}
