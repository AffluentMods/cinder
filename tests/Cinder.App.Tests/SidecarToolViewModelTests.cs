using Cinder.App.ViewModels.Tools;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// The shared parser-tool base: one run at a time, a superseded run can change nothing,
/// stopping is not an error, failures reach the error banner, and the row filter behaves the
/// same on small and large grids.
/// </summary>
public class SidecarToolViewModelTests
{
    /// <summary>A tool whose parser is driven by the test: each path completes when the test says so.</summary>
    private sealed class FakeTool : SidecarToolViewModel
    {
        public override string Id => "fake";
        public override string Title => "Fake";
        public override string Icon => "";
        public override string Kind => "fake";

        public Dictionary<string, TaskCompletionSource<ParseResult>> Pending { get; } = [];

        /// <summary>When true the parser stops when asked. When false it ignores the request, as a parser stuck in library code would.</summary>
        public bool HonourCancellation { get; set; }

        protected override string? CheckInput(string path) => null;

        protected override Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ParseResult>();
            Pending[evidencePath] = tcs;
            if (HonourCancellation)
            {
                ct.Register(() => tcs.TrySetCanceled(ct));
            }
            return tcs.Task;
        }

        public Task Start(string path) => LoadEvidenceCommand.ExecuteAsync(path);

        public void Clear(string? status) => ClearResults(status);
    }

    private static List<object> Rows(string tag, int count) =>
        [.. Enumerable.Range(0, count).Select(i => (object)new { File = $"{tag}-{i}", Size = i })];

    [Fact]
    public async Task A_superseded_run_that_finishes_late_cannot_put_its_rows_in_the_grid_Async()
    {
        var tool = new FakeTool();
        var first = tool.Start("a.dat");
        var second = tool.Start("b.dat");

        tool.Pending["b.dat"].SetResult(new ParseResult(Rows("b", 3)));
        await second;
        // Run 1 ignored the cancellation and comes back afterwards with file A's rows.
        tool.Pending["a.dat"].SetResult(new ParseResult(Rows("a", 5), Truncated: true));
        await first;

        tool.EvidencePath.Should().Be("b.dat");
        tool.Rows.Should().HaveCount(3);
        tool.Rows.Select(r => Cinder.Core.Export.TabularExporter.ToDictionary(r)["File"]).Should().OnlyContain(f => f!.StartsWith("b-"));
        tool.VisibleRows.Should().HaveCount(3);
        tool.IsTruncated.Should().BeFalse("run 1's truncation flag belongs to run 1");
        tool.ErrorMessage.Should().BeNull();
        tool.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task A_superseded_run_that_is_cancelled_shows_no_error_and_leaves_the_new_run_busy_Async()
    {
        var tool = new FakeTool { HonourCancellation = true };
        var first = tool.Start("a.dat");
        var second = tool.Start("b.dat");   // cancels run 1
        await first;

        // The old behaviour: "The operation was canceled." in the error banner over file B,
        // and the busy flag cleared while B was still being read.
        tool.ErrorMessage.Should().BeNull();
        tool.IsLoading.Should().BeTrue("run 2 is still reading");
        tool.StatusLine.Should().StartWith("Reading b.dat");

        tool.Pending["b.dat"].SetResult(new ParseResult(Rows("b", 2)));
        await second;
        tool.IsLoading.Should().BeFalse();
        tool.Rows.Should().HaveCount(2);
        tool.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Stopping_a_run_says_stopped_and_is_not_an_error_Async()
    {
        var tool = new FakeTool { HonourCancellation = true };
        var run = tool.Start("a.dat");
        tool.IsLoading.Should().BeTrue();

        tool.LoadEvidenceCancelCommand.Execute(null);
        await run;

        tool.StatusLine.Should().StartWith("Stopped");
        tool.ErrorMessage.Should().BeNull();
        tool.IsLoading.Should().BeFalse();
        tool.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_parser_failure_reaches_the_error_banner_and_clears_the_previous_files_rows_Async()
    {
        var tool = new FakeTool();
        var ok = tool.Start("good.dat");
        tool.Pending["good.dat"].SetResult(new ParseResult(Rows("g", 1204)));
        await ok;
        tool.RowCountDisplay.Should().Be("1,204 rows");

        var bad = tool.Start("SOFTWARE");
        tool.Pending["SOFTWARE"].SetException(new InvalidOperationException("SOFTWARE has no ControlSet keys, so it is not a SYSTEM hive."));
        await bad;

        tool.ErrorMessage.Should().Be("SOFTWARE has no ControlSet keys, so it is not a SYSTEM hive.");
        tool.ErrorTitle.Should().Be("Could not read this file");
        tool.StatusLine.Should().Be("Could not read SOFTWARE.");
        tool.Rows.Should().BeEmpty();
        tool.RowCountDisplay.Should().Be("0 rows", "the footer must not keep the previous file's count");
        tool.ShowNoRows.Should().BeFalse("an error is not 'nothing found'");
        tool.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_result_says_why_instead_of_looking_like_a_blank_grid_Async()
    {
        var tool = new FakeTool();
        var run = tool.Start("SYSTEM");
        tool.Pending["SYSTEM"].SetResult(new ParseResult([], Note: "This SYSTEM hive has no Enum\\USBSTOR key: no USB storage device is recorded in it."));
        await run;

        tool.ShowNoRows.Should().BeTrue();
        tool.StatusLine.Should().Contain("no USB storage device is recorded");
        tool.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task A_truncated_result_keeps_its_warning_when_the_status_line_moves_on_Async()
    {
        var tool = new FakeTool();
        var run = tool.Start("journal");
        tool.Pending["journal"].SetResult(new ParseResult(Rows("j", 10), Truncated: true, AvailableRows: 500));
        await run;

        tool.IsTruncated.Should().BeTrue();
        tool.TruncationNotice.Should().Contain("first 10 rows of 500").And.Contain("not the whole artifact");

        tool.StatusLine = "Bookmarked #3";
        tool.TruncationNotice.Should().Contain("not the whole artifact");
    }

    [Fact]
    public async Task Clearing_results_supersedes_a_run_in_flight_Async()
    {
        var tool = new FakeTool();
        var run = tool.Start("list.txt");
        tool.Clear("The folder changed. Run the scan again.");
        tool.Pending["list.txt"].SetResult(new ParseResult(Rows("x", 4)));
        await run;

        tool.Rows.Should().BeEmpty();
        tool.IsLoading.Should().BeFalse();
        tool.StatusLine.Should().Be("The folder changed. Run the scan again.");
    }

    // ---------------------------------------------------------------- Filter

    private static async Task<FakeTool> LoadedAsync(List<object> rows)
    {
        var tool = new FakeTool();
        var run = tool.Start("f");
        tool.Pending["f"].SetResult(new ParseResult(rows));
        await run;
        return tool;
    }

    [Fact]
    public async Task Filter_matches_any_column_ignoring_case_Async()
    {
        var tool = await LoadedAsync(
        [
            new { Name = "Invoice.PDF", Owner = "alice", Size = 10 },
            new { Name = "notes.txt", Owner = "Bob", Size = 2048 },
            new { Name = "photo.jpg", Owner = "carol", Size = 77 },
        ]);

        tool.RowFilter = "pdf";
        tool.VisibleRows.Should().ContainSingle();

        tool.RowFilter = "BOB";
        tool.VisibleRows.Should().ContainSingle();

        tool.RowFilter = "2048";
        tool.VisibleRows.Should().ContainSingle("numbers are matched by their text");
        tool.RowCountDisplay.Should().Be("1 of 3 rows");

        tool.RowFilter = "  ";
        tool.VisibleRows.Should().HaveCount(3);
        tool.RowCountDisplay.Should().Be("3 rows");
    }

    [Fact]
    public async Task Filter_does_not_match_text_that_only_exists_across_two_columns_Async()
    {
        var tool = await LoadedAsync([new { First = "abc", Second = "def" }]);

        tool.RowFilter = "cd";
        tool.VisibleRows.Should().BeEmpty("\"cd\" is the end of one column and the start of the next, not a value");

        tool.RowFilter = "bc";
        tool.VisibleRows.Should().ContainSingle();
    }

    [Fact]
    public async Task A_filter_typed_before_loading_is_applied_to_the_new_rows_Async()
    {
        var tool = new FakeTool { RowFilter = "b-1" };
        var run = tool.Start("f");
        tool.Pending["f"].SetResult(new ParseResult(Rows("b", 30)));
        await run;

        // b-1, b-10 … b-19
        tool.Rows.Should().HaveCount(30);
        tool.VisibleRows.Should().HaveCount(11);
    }

    [Fact]
    public async Task On_a_large_grid_the_filter_applies_after_a_pause_and_gives_the_same_answer_Async()
    {
        var rows = Rows("row", SidecarToolViewModel.ImmediateFilterRows + 500);
        var tool = await LoadedAsync(rows);
        var expected = SidecarToolViewModel.FilterRows(rows, SidecarToolViewModel.BuildSearchText(rows), "row-12");

        tool.RowFilter = "row-1";      // superseded by the next keystroke
        tool.RowFilter = "row-12";

        for (var i = 0; i < 200 && tool.VisibleRows.Count != expected.Count; i++)
        {
            await Task.Delay(25);
        }
        tool.VisibleRows.Should().Equal(expected);
        tool.RowCountDisplay.Should().Be($"{expected.Count:N0} of {rows.Count:N0} rows");
    }

    [Fact]
    public void Search_text_is_built_from_the_same_values_the_export_writes()
    {
        var when = new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.FromHours(1));
        var rows = new List<object> { new { Name = "a", When = when, Count = 1234, Missing = (string?)null } };

        var text = SidecarToolViewModel.BuildSearchText(rows)[0];

        // What the examiner filters on is what the exported file contains.
        var exported = Cinder.Core.Export.TabularExporter.ToDictionary(rows[0]);
        text.Should().Contain(exported["When"]).And.Contain("1234").And.Contain("a");
        text.Split(SidecarToolViewModel.ColumnSeparator).Should().HaveCount(3, "an empty cell adds nothing to match");
        SidecarToolViewModel.FilterRows(rows, [text], "2024-03-01T11:00").Should().ContainSingle();
    }

    [Fact]
    public void Drop_hint_and_input_check_follow_what_the_tool_accepts()
    {
        new PrefetchTool().DropHint.Should().Be("or drop a file or folder here");
        new RegistryTool().DropHint.Should().Be("or drop a file here");
        new PrefetchTool().AcceptsFolder.Should().BeTrue();
        new MobileTool().AcceptsFolder.Should().BeTrue();
        new RecycleBinTool().AcceptsFolder.Should().BeTrue();
        new LinuxArtifactsTool().AcceptsFolder.Should().BeTrue();
        new BrowserHistoryTool().AcceptsFolder.Should().BeTrue();
        new JumplistsTool().AcceptsFolder.Should().BeTrue();
        new LnkTool().AcceptsFolder.Should().BeTrue();
        new RegistryTool().AcceptsFolder.Should().BeFalse();
    }

    [Fact]
    public async Task A_folder_given_to_a_file_only_tool_is_refused_with_a_reason_Async()
    {
        var tool = new RegistryTool();
        await tool.LoadEvidenceCommand.ExecuteAsync(Path.GetTempPath());

        tool.ErrorMessage.Should().Contain("reads a single file");
        tool.ErrorTitle.Should().Be("Could not read this folder");
        tool.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_registry_tool_given_the_wrong_kind_of_file_shows_an_error_not_zero_rows_Async()
    {
        var file = Path.Combine(Path.GetTempPath(), "cinder-not-a-hive-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(file, "plain text, not a registry hive");
        try
        {
            foreach (SidecarToolViewModel tool in new SidecarToolViewModel[] { new UsbHistoryTool(), new WifiHistoryTool(), new AmcacheTool(), new ShimcacheTool(), new ShellbagsTool() })
            {
                await tool.LoadEvidenceCommand.ExecuteAsync(file);

                tool.ErrorMessage.Should().NotBeNullOrEmpty($"{tool.Title} must not report an unreadable file as an empty result");
                tool.ErrorMessage.Should().Contain("registry hive");
                tool.Rows.Should().BeEmpty();
                tool.ShowNoRows.Should().BeFalse();
            }
        }
        finally
        {
            File.Delete(file);
        }
    }
}
