using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Cinder.Cloud;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// The cloud connectors against canned provider responses. Each test pins a way a collection
/// used to come back short or mislabelled without any error: a provider digest presented as a
/// SHA-256, a listing that stopped at the first folder level, a history that stopped at the
/// first page.
/// </summary>
public sealed class CloudConnectorTests
{
    private static readonly CloudAuthorization Auth = new("test", "account-1", "token-abc", null, null, "scope");

    /// <summary>Answers each request from a queue and records what was asked.</summary>
    private sealed class ScriptedHandler(params Func<HttpRequestMessage, string>[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<(HttpMethod Method, Uri Uri, string Body, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString()));
            if (_next >= responses.Length)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("unexpected extra request") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[_next++](request), Encoding.UTF8, "application/json"),
            };
        }
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }
        return list;
    }

    // ---- Dropbox -----------------------------------------------------------------------------

    [Fact]
    public async Task Dropbox_content_hash_is_not_reported_as_the_files_sha256()
    {
        // Dropbox's documented content_hash for an empty file's neighbour: 64 hex characters
        // that are SHA-256(SHA-256(block)...), not SHA-256(file).
        const string ContentHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var handler = new ScriptedHandler(_ => $$"""
            {"entries":[{".tag":"file","id":"id:1","path_display":"/Docs/a.txt","size":10,
                         "client_modified":"2024-05-01T10:00:00Z","content_hash":"{{ContentHash}}"},
                        {".tag":"folder","id":"id:2","path_display":"/Docs"}],
             "has_more":false,"cursor":"c"}
            """);
        var connector = new DropboxConnector(new HttpClient(handler));

        var files = await Collect(connector.ListFilesAsync(Auth, CancellationToken.None));

        var file = files.Single(f => f.Path == "/Docs/a.txt");
        file.Sha256.Should().BeNull("a provider digest in the Sha256 field makes every hash-set comparison miss");
        file.ProviderHash.Should().Be(ContentHash);
        file.ProviderHashAlgorithm.Should().Be(DropboxConnector.ContentHashAlgorithm);
        files.Single(f => f.Path == "/Docs").ProviderHash.Should().BeNull();
    }

    [Fact]
    public async Task Dropbox_listing_follows_the_cursor()
    {
        var handler = new ScriptedHandler(
            _ => """{"entries":[{"id":"id:1","path_display":"/a","size":1}],"has_more":true,"cursor":"CUR1"}""",
            _ => """{"entries":[{"id":"id:2","path_display":"/b","size":2}],"has_more":false,"cursor":"CUR2"}""");
        var connector = new DropboxConnector(new HttpClient(handler));

        var files = await Collect(connector.ListFilesAsync(Auth, CancellationToken.None));

        files.Select(f => f.Path).Should().Equal("/a", "/b");
        handler.Requests[1].Uri.AbsolutePath.Should().EndWith("/list_folder/continue");
        JsonNode.Parse(handler.Requests[1].Body)!["cursor"]!.GetValue<string>().Should().Be("CUR1");
    }

    // ---- OneDrive ----------------------------------------------------------------------------

    [Fact]
    public async Task OneDrive_lists_files_in_subfolders_not_only_the_root_level()
    {
        var handler = new ScriptedHandler(
            _ => """
                {"value":[
                   {"id":"root","name":"root","root":{},"folder":{"childCount":2}},
                   {"id":"f1","name":"Evidence","folder":{"childCount":1},"parentReference":{"path":"/drive/root:"}},
                   {"id":"a","name":"top.txt","size":5,"file":{"hashes":{"sha256Hash":"AA11","quickXorHash":"qx=="}},
                    "createdDateTime":"2024-01-01T00:00:00Z","lastModifiedDateTime":"2024-01-02T00:00:00Z",
                    "parentReference":{"path":"/drive/root:"}}],
                 "@odata.nextLink":"https://graph.microsoft.com/v1.0/me/drive/root/delta?token=PAGE2"}
                """,
            _ => """
                {"value":[
                   {"id":"b","name":"deep file.docx","size":7,"file":{"hashes":{"quickXorHash":"zz=="}},
                    "parentReference":{"path":"/drive/root:/Evidence/Sub%20Folder"}},
                   {"id":"c","name":"gone.txt","deleted":{"state":"deleted"},"file":{},
                    "parentReference":{"path":"/drive/root:/Evidence"}}],
                 "@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/drive/root/delta?token=FINAL"}
                """);
        var connector = new OneDriveConnector(new HttpClient(handler));

        var files = await Collect(connector.ListFilesAsync(Auth, CancellationToken.None));

        handler.Requests[0].Uri.AbsolutePath.Should().Be("/v1.0/me/drive/root/delta",
            "root/children returns one folder level; delta enumerates the whole drive");
        handler.Requests.Should().HaveCount(2, "the next-page link is followed and the delta link is not");
        handler.Requests[1].Uri.Query.Should().Contain("PAGE2");
        handler.Requests.Should().OnlyContain(r => r.Authorization == "Bearer token-abc");

        files.Select(f => f.Path).Should().Equal("/top.txt", "/Evidence/Sub Folder/deep file.docx");
        files[0].Sha256.Should().Be("AA11");
        files[1].Sha256.Should().BeNull("business drives publish only QuickXorHash");
        files[1].ProviderHash.Should().Be("zz==");
        files[1].ProviderHashAlgorithm.Should().Be(OneDriveConnector.QuickXorHashAlgorithm);
    }

    [Fact]
    public async Task OneDrive_does_not_send_the_token_to_a_next_link_outside_graph()
    {
        var handler = new ScriptedHandler(
            _ => """{"value":[],"@odata.nextLink":"https://attacker.example/steal"}""",
            _ => """{"value":[]}""");
        var connector = new OneDriveConnector(new HttpClient(handler));

        var act = async () => await Collect(connector.ListFilesAsync(Auth, CancellationToken.None));

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().HaveCount(1);
    }

    // ---- Google Drive ------------------------------------------------------------------------

    [Fact]
    public async Task Google_activity_is_read_to_the_last_page()
    {
        static string Page(int first, int count, string? next) =>
            new JsonObject
            {
                ["activities"] = new JsonArray(Enumerable.Range(first, count).Select(i => (JsonNode?)new JsonObject
                {
                    ["timestamp"] = "2024-06-01T00:00:00Z",
                    ["primaryActionDetail"] = new JsonObject { ["edit"] = new JsonObject() },
                    ["targets"] = new JsonArray(new JsonObject { ["driveItem"] = new JsonObject { ["title"] = $"file-{i}" } }),
                }).ToArray()),
                ["nextPageToken"] = next,
            }.ToJsonString();

        var handler = new ScriptedHandler(
            _ => Page(0, 500, "TOKEN-2"),
            _ => Page(500, 500, "TOKEN-3"),
            _ => Page(1000, 7, null));
        var connector = new GoogleDriveConnector(new HttpClient(handler));

        var activity = await Collect(connector.ListActivityAsync(Auth, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(2)), CancellationToken.None));

        activity.Should().HaveCount(1007, "an account's history does not end at the first 500 events");
        activity[^1].Target.Should().Be("file-1006");
        activity[0].Action.Should().Be("edit");
        handler.Requests.Should().HaveCount(3);
        JsonNode.Parse(handler.Requests[0].Body)!["pageToken"].Should().BeNull();
        JsonNode.Parse(handler.Requests[1].Body)!["pageToken"]!.GetValue<string>().Should().Be("TOKEN-2");
        JsonNode.Parse(handler.Requests[2].Body)!["pageToken"]!.GetValue<string>().Should().Be("TOKEN-3");
        JsonNode.Parse(handler.Requests[0].Body)!["filter"]!.GetValue<string>()
            .Should().Be("time >= \"2023-12-31T22:00:00.000Z\"", "the bound is sent in UTC");
    }

    [Fact]
    public async Task Google_activity_without_a_target_or_timestamp_is_still_returned()
    {
        var handler = new ScriptedHandler(_ => """{"activities":[{"primaryActionDetail":{"delete":{}},"targets":[]}]}""");
        var connector = new GoogleDriveConnector(new HttpClient(handler));

        var activity = await Collect(connector.ListActivityAsync(Auth, DateTimeOffset.UnixEpoch, CancellationToken.None));

        var only = activity.Should().ContainSingle().Subject;
        only.Action.Should().Be("delete");
        only.Target.Should().BeEmpty();
        only.Timestamp.Should().BeNull("an unknown time must not be reported as year 0001");
    }

    [Fact]
    public async Task Google_file_listing_escapes_the_page_token()
    {
        var handler = new ScriptedHandler(
            _ => """{"files":[{"id":"1","name":"a","size":"3"}],"nextPageToken":"a+b/c=&x"}""",
            _ => """{"files":[{"id":"2","name":"b"}]}""");
        var connector = new GoogleDriveConnector(new HttpClient(handler));

        var files = await Collect(connector.ListFilesAsync(Auth, CancellationToken.None));

        files.Select(f => f.Path).Should().Equal("a", "b");
        handler.Requests[1].Uri.Query.Should().Contain("pageToken=a%2Bb%2Fc%3D%26x");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Google_token_exchange_uses_the_verifier_from_begin_auth_when_none_is_passed(string? passed)
    {
        var handler = new ScriptedHandler(_ => """{"access_token":"at","refresh_token":"rt","expires_in":3600,"scope":"s"}""");
        var connector = new GoogleDriveConnector(new HttpClient(handler)) { ClientId = "client" };
        var authUrl = await connector.BeginAuthAsync("http://127.0.0.1:9999/", CancellationToken.None);
        authUrl.Query.Should().Contain("code_challenge=");

        var authorization = await connector.CompleteAuthAsync("the-code", passed!, "http://127.0.0.1:9999/", CancellationToken.None);

        authorization.AccessToken.Should().Be("at");
        var form = System.Web.HttpUtility.ParseQueryString(handler.Requests.Single().Body);
        form["code_verifier"].Should().NotBeNullOrEmpty("an empty verifier fails the PKCE check at the provider");
        form["code_verifier"]!.Length.Should().BeGreaterThanOrEqualTo(43, "RFC 7636 §4.1");
        form["code"].Should().Be("the-code");
    }
}
