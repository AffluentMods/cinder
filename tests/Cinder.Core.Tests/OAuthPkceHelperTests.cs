using System.Net;
using System.Net.Sockets;
using Cinder.Cloud;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

public sealed class OAuthPkceHelperTests
{
    [Fact]
    public void State_and_verifier_are_fresh_and_url_safe()
    {
        var s1 = OAuthPkceHelper.GenerateState();
        var s2 = OAuthPkceHelper.GenerateState();
        s1.Should().NotBe(s2);
        s1.Length.Should().BeGreaterThan(40);
        s1.Should().MatchRegex("^[A-Za-z0-9_-]+$");

        var (verifier, challenge) = OAuthPkceHelper.GeneratePkcePair();
        verifier.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        challenge.Should().NotBe(verifier);
    }

    [Theory]
    [InlineData("http://127.0.0.1:53211/", true)]
    [InlineData("http://localhost:53211/callback", true)]
    [InlineData("http://[::1]:53211/", true)]
    [InlineData("http://0.0.0.0:53211/", false)]
    [InlineData("http://+:53211/", false)]
    [InlineData("http://192.168.1.5:53211/", false)]
    [InlineData("https://127.0.0.1:53211/", false)]
    [InlineData("not a url", false)]
    public void Only_loopback_http_prefixes_may_be_bound(string prefix, bool ok)
        => OAuthPkceHelper.IsLoopbackPrefix(prefix).Should().Be(ok);

    [Fact]
    public async Task Listener_refuses_a_non_loopback_prefix_before_binding()
    {
        var act = async () => await OAuthPkceHelper.AwaitRedirectCodeAsync("http://0.0.0.0:1/", "state", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Callback_with_matching_state_returns_the_code()
    {
        var (prefix, url) = FreeLoopback();
        var state = OAuthPkceHelper.GenerateState();
        var listen = OAuthPkceHelper.AwaitRedirectCodeAsync(prefix, state, CancellationToken.None);

        using var http = new HttpClient();
        var resp = await http.GetAsync($"{url}?code=the-code&state={state}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        (await listen).Should().Be("the-code");
    }

    [Fact]
    public async Task Callback_with_wrong_state_is_rejected_and_the_code_is_not_returned()
    {
        // The CSRF case: something other than the sign-in Cinder started hits the listener.
        var (prefix, url) = FreeLoopback();
        var listen = OAuthPkceHelper.AwaitRedirectCodeAsync(prefix, OAuthPkceHelper.GenerateState(), CancellationToken.None);

        using var http = new HttpClient();
        var resp = await http.GetAsync($"{url}?code=attacker-code&state=forged");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var act = async () => await listen;
        await act.Should().ThrowAsync<OAuthRedirectException>().WithMessage("*state*");
    }

    [Fact]
    public async Task Provider_error_is_surfaced_not_swallowed()
    {
        var (prefix, url) = FreeLoopback();
        var state = OAuthPkceHelper.GenerateState();
        var listen = OAuthPkceHelper.AwaitRedirectCodeAsync(prefix, state, CancellationToken.None);

        using var http = new HttpClient();
        await http.GetAsync($"{url}?error=access_denied&error_description=user+said+no&state={state}");

        var act = async () => await listen;
        await act.Should().ThrowAsync<OAuthRedirectException>().WithMessage("*access_denied*");
    }

    [Fact]
    public async Task Missing_code_is_an_error_even_with_the_right_state()
    {
        var (prefix, url) = FreeLoopback();
        var state = OAuthPkceHelper.GenerateState();
        var listen = OAuthPkceHelper.AwaitRedirectCodeAsync(prefix, state, CancellationToken.None);

        using var http = new HttpClient();
        await http.GetAsync($"{url}?state={state}");

        var act = async () => await listen;
        await act.Should().ThrowAsync<OAuthRedirectException>().WithMessage("*no authorization code*");
    }

    private static (string Prefix, string Url) FreeLoopback()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return ($"http://127.0.0.1:{port}/cinder-oauth/", $"http://127.0.0.1:{port}/cinder-oauth/");
    }
}
