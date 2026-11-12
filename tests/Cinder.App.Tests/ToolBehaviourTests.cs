using System.Runtime.CompilerServices;
using System.Text.Json;
using Cinder.App.ViewModels;
using Cinder.App.ViewModels.Tools;
using Cinder.Workflow;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// Smaller behaviours of individual tools: the AI endpoint check, workflow steps that must fail
/// loudly, the gallery's stated cap, and the wording rules for every tool's subtitle, hint and help.
/// </summary>
public class ToolBehaviourTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-tests-" + Guid.NewGuid().ToString("N"));

    public ToolBehaviourTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---------------------------------------------------------------- AI endpoint

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("  https://api.example.com/v1  ")]
    [InlineData("http://192.168.1.5:11434/")]
    public void A_full_http_address_is_accepted(string text)
    {
        AiCopilotToolViewModel.TryParseEndpoint(text, out var uri, out var problem).Should().BeTrue();
        uri!.Host.Should().NotBeEmpty();
        problem.Should().BeNull();
    }

    [Theory]
    // Each of these used to throw out of the Send command and end the application.
    [InlineData("192.168.1.5:11434", "Did you mean http://192.168.1.5:11434")]
    [InlineData("localhost:11434", "Did you mean http://localhost:11434")]
    [InlineData("localhost", "Did you mean http://localhost")]
    [InlineData("ftp://example.com", "http:// or https://")]
    [InlineData("", "Enter the server address")]
    [InlineData(null, "Enter the server address")]
    [InlineData("   ", "Enter the server address")]
    public void Anything_else_is_refused_with_a_sentence_that_says_what_to_type(string? text, string expectedInMessage)
    {
        AiCopilotToolViewModel.TryParseEndpoint(text, out var uri, out var problem).Should().BeFalse();
        uri.Should().BeNull();
        problem.Should().Contain(expectedInMessage);
    }

    [Fact]
    public async Task Sending_with_a_bad_address_keeps_the_question_and_explains_Async()
    {
        var vm = new AiCopilotToolViewModel
        {
            ProviderId = "ollama",
            Endpoint = "192.168.1.5:11434",
            Model = "llama3",
            Input = "What does event 4769 mean?",
        };

        await vm.SendCommand.ExecuteAsync(null);

        vm.Input.Should().Be("What does event 4769 mean?", "nothing was sent, so the question stays in the box");
        vm.Messages.Should().BeEmpty();
        vm.IsStreaming.Should().BeFalse();
        vm.StatusLine.Should().Contain("not a full address");
    }

    // ---------------------------------------------------------------- Workflow steps

    private static WorkflowNode Node(string id, string kind, string? path = null)
    {
        var node = new WorkflowNode { Id = id, Kind = kind };
        if (path is not null)
        {
            node.Parameters["path"] = JsonSerializer.SerializeToElement(path);
        }
        return node;
    }

    private static (WorkflowRunner Runner, List<WorkflowExecutionRow> Rows) Runner()
    {
        var rows = new List<WorkflowExecutionRow>();
        return (WorkflowHandlers.BuildRunner(rows.Add), rows);
    }

    [Fact]
    public async Task A_step_whose_input_is_missing_stops_the_run_and_names_itself_Async()
    {
        var (runner, rows) = Runner();
        var ran = Path.Combine(_dir, "report.md");
        var workflow = new Cinder.Workflow.Workflow
        {
            Nodes = [Node("hash-image", "hash", Path.Combine(_dir, "missing.dd")), Node("write-report", "report", ran)],
            Edges = [new WorkflowEdge { From = "hash-image", To = "write-report" }],
        };

        var act = () => runner.RunAsync(workflow);

        // The old handler reported "failed", returned, and the run carried on to "Done".
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*hash-image*").WithMessage("*(hash)*").WithMessage("*does not exist*");
        rows.Should().BeEmpty("no step did any work");
        File.Exists(ran).Should().BeFalse("the step after the failure must not run");
    }

    [Fact]
    public async Task Listing_files_in_something_that_is_not_a_disk_image_is_a_failure_not_ok_zero_Async()
    {
        var notAnImage = Path.Combine(_dir, "notes.bin");
        await File.WriteAllBytesAsync(notAnImage, new byte[64 * 1024]);
        var (runner, rows) = Runner();
        var workflow = new Cinder.Workflow.Workflow
        {
            Nodes = [Node("open", "open-image", notAnImage), Node("list", "fs-enumerate")],
            Edges = [new WorkflowEdge { From = "open", To = "list" }],
        };

        var act = () => runner.RunAsync(workflow);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*\"list\"*");
        rows.Should().ContainSingle().Which.Should().Match<WorkflowExecutionRow>(r => r.NodeId == "open" && r.Status == "ok");
    }

    [Fact]
    public async Task A_step_without_a_path_and_nothing_before_it_says_so_Async()
    {
        var (runner, _) = Runner();
        var act = () => runner.RunAsync(new Cinder.Workflow.Workflow { Nodes = [Node("h", "hash")] });
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*no earlier step produced a file*");
    }

    [Fact]
    public async Task Steps_that_succeed_report_what_they_did_and_pass_their_file_on_Async()
    {
        var file = Path.Combine(_dir, "evidence.bin");
        await File.WriteAllTextAsync(file, "abc");
        var (runner, rows) = Runner();
        var workflow = new Cinder.Workflow.Workflow
        {
            Nodes = [Node("open", "open-image", file), Node("digest", "hash"), Node("summary", "ai-summary")],
            Edges = [new WorkflowEdge { From = "open", To = "digest" }, new WorkflowEdge { From = "digest", To = "summary" }],
        };

        var outputs = await runner.RunAsync(workflow);

        // SHA-256 of "abc".
        outputs["digest"].Should().BeOfType<string>().Which.Should()
            .BeEquivalentTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        rows.Select(r => (r.NodeId, r.Status)).Should().Equal(("open", "ok"), ("digest", "ok"), ("summary", "skipped"));
    }

    // ---------------------------------------------------------------- Gallery

    [Fact]
    public void Gallery_reports_when_a_folder_holds_more_images_than_it_shows()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        for (var i = 0; i < 4; i++)
        {
            File.WriteAllBytes(Path.Combine(_dir, $"p{i}.JPG"), [1]);
        }
        File.WriteAllBytes(Path.Combine(_dir, "sub", "deep.png"), [1]);
        File.WriteAllBytes(Path.Combine(_dir, "notes.txt"), [1]);

        var (all, moreThanAll) = GalleryTool.FindImages(_dir, max: 10, CancellationToken.None);
        all.Should().HaveCount(5, "extensions match whatever their case, and subfolders are searched");
        moreThanAll.Should().BeFalse();

        var (exact, moreThanExact) = GalleryTool.FindImages(_dir, max: 5, CancellationToken.None);
        exact.Should().HaveCount(5);
        moreThanExact.Should().BeFalse("exactly the cap is everything");

        var (some, more) = GalleryTool.FindImages(_dir, max: 3, CancellationToken.None);
        some.Should().HaveCount(3);
        more.Should().BeTrue();
    }

    [Fact]
    public void Gallery_status_states_the_cap_when_it_was_reached()
    {
        GalleryTool.DescribeLoad(500, more: true).Should().Contain("first 500 images").And.Contain("more than that");
        GalleryTool.DescribeLoad(500, more: false).Should().Be("500 images.");
        GalleryTool.DescribeLoad(1, more: false).Should().Be("1 image.");
        GalleryTool.DescribeLoad(0, more: false).Should().StartWith("No images were found");
    }

    // ---------------------------------------------------------------- Wording

    /// <summary>
    /// Every tool, created without running its constructor: these properties are constant
    /// text, and some constructors read settings or open databases.
    /// </summary>
    public static TheoryData<string> ToolTypes()
    {
        var data = new TheoryData<string>();
        foreach (var t in typeof(ToolViewModel).Assembly.GetTypes()
                     .Where(t => !t.IsAbstract && typeof(ToolViewModel).IsAssignableFrom(t))
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            data.Add(t.FullName!);
        }
        return data;
    }

    private static ToolViewModel Blank(string typeName) =>
        (ToolViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ToolViewModel).Assembly.GetType(typeName)!);

    private static readonly string[] DeveloperWords =
    [
        "in-process", "sidecar", "DiscUtils", "python-evtx", "pylnk3", "libesedb", "Lucene", "SQLite", "BYOM",
        " via ", "v0.", "next pass", "wired up", "back end", "court-ready",
    ];

    [Theory]
    [MemberData(nameof(ToolTypes))]
    public void Subtitle_is_one_plain_sentence_about_what_the_tool_shows(string typeName)
    {
        var subtitle = Blank(typeName).Subtitle;

        subtitle.Should().NotBeNullOrWhiteSpace();
        subtitle.Should().EndWith(".");
        subtitle.Should().MatchRegex("^[A-Z]", "sentence case");
        subtitle.Should().NotContain(" · ", "a subtitle is a sentence, not a feature list");
        subtitle.TrimEnd('.').Should().NotContain(". ", "one sentence");
        subtitle.Should().NotContainAny(DeveloperWords);
    }

    [Theory]
    [MemberData(nameof(ToolTypes))]
    public void Every_tool_that_reads_picked_evidence_says_what_to_pick(string typeName)
    {
        if (Blank(typeName) is not SidecarToolViewModel tool)
        {
            return;
        }

        tool.EmptyStateHint.Should().NotBeNullOrWhiteSpace($"{tool.Title} needs its own hint");
        tool.EmptyStateHint.Should().NotBe("Load evidence to populate this tool.");
        tool.EmptyStateHint.Should().StartWith("Pick ").And.EndWith(".");
        tool.EmptyStateHint.Should().NotContainAny(DeveloperWords);
        if (tool.AcceptsFolder)
        {
            tool.EmptyStateHint.Should().Contain("folder", $"{tool.Title} can read a folder, so the hint must say so");
            tool.DropHint.Should().Contain("folder");
        }
    }

    [Theory]
    [MemberData(nameof(ToolTypes))]
    public void Help_makes_no_claim_the_tools_do_not_keep(string typeName)
    {
        var help = Blank(typeName).HelpMarkdown;

        help.Should().NotBeNullOrWhiteSpace();
        help.Should().NotContain("court-ready");
        help.Should().NotContain("export to get everything").And.NotContain("export to see the rest");
        help.Should().NotContain("Ctrl+Alt+B", "the Inspector shortcut is Ctrl+Shift+B");
        help.Should().NotContain("chain of custody log appended");
        // Features the help used to promise and the tools do not have.
        help.Should().NotContain("first plug-in").And.NotContain("JA3").And.NotContain("Right-click an");
    }

    [Fact]
    public void Home_guide_describes_only_what_the_app_does()
    {
        var guide = new DashboardTool().Guide;

        guide.Should().HaveCount(5);
        var text = string.Join(" ", guide.Select(g => g.Title + " " + g.Body));
        text.Should().NotContain("court-ready").And.NotContain("appended automatically").And.NotContain("hashes it on import");
        text.Should().Contain("Each export is recorded in the case's custody log");
    }
}
