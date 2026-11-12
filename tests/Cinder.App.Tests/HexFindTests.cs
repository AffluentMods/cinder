using System.Text;
using Cinder.App.ViewModels;
using Cinder.Hex;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

public class HexFindTests
{
    /// <summary>"needle" at offsets 100, 2000 and 3500 of a 4096-byte buffer of dots.</summary>
    private static HexViewModel WithNeedles()
    {
        var bytes = Enumerable.Repeat((byte)'.', 4096).ToArray();
        foreach (var at in new[] { 100, 2000, 3500 })
        {
            Encoding.ASCII.GetBytes("needle").CopyTo(bytes, at);
        }
        return new HexViewModel { Buffer = new ByteArrayHexBuffer(bytes, "needles.bin") };
    }

    private static HexViewModel With(byte[] bytes) => new() { Buffer = new ByteArrayHexBuffer(bytes, "test.bin") };

    // ------------------------------------------------------------------ find from the caret

    [Theory]
    [InlineData(0, 100, "1 of 3")]
    [InlineData(100, 100, "1 of 3")]
    [InlineData(101, 2000, "2 of 3")]
    [InlineData(2500, 3500, "3 of 3")]
    [InlineData(3501, 100, "1 of 3")]     // nothing after the caret: wrap to the first
    public async Task The_first_find_lands_on_the_first_hit_at_or_after_the_caret(long caret, long expectedHit, string status)
    {
        using var hex = WithNeedles();
        hex.CaretOffset = caret;
        hex.FindQuery = "needle";

        await hex.RunFindCommand.ExecuteAsync(null);

        hex.SearchResults.Select(h => h.Offset).Should().Equal(100, 2000, 3500);
        hex.CaretOffset.Should().Be(expectedHit);
        hex.SelectionStart.Should().Be(expectedHit);
        hex.SelectionEnd.Should().Be(expectedHit + 5);
        hex.FindStatus.Should().Be(status);
    }

    [Fact]
    public async Task Next_and_previous_step_and_wrap_at_both_ends()
    {
        using var hex = WithNeedles();
        hex.FindQuery = "needle";
        await hex.RunFindCommand.ExecuteAsync(null);
        hex.CaretOffset.Should().Be(100);

        hex.FindPreviousCommand.Execute(null);
        hex.CaretOffset.Should().Be(3500);
        hex.FindStatus.Should().Be("3 of 3");

        hex.FindNextCommand.Execute(null);
        hex.CaretOffset.Should().Be(100);
        hex.FindStatus.Should().Be("1 of 3");

        hex.FindNextCommand.Execute(null);
        hex.FindNextCommand.Execute(null);
        hex.CaretOffset.Should().Be(3500);
        hex.FindNextCommand.Execute(null);
        hex.CaretOffset.Should().Be(100);
    }

    [Fact]
    public async Task Searching_does_not_move_the_caret_by_itself()
    {
        using var hex = WithNeedles();
        hex.CaretOffset = 3000;

        await hex.SearchAsync(new HexSearchOptions(HexSearchKind.Ascii, "needle"));

        hex.SearchResults.Should().HaveCount(3);
        hex.CaretOffset.Should().Be(3000, "where to land is the caller's decision, not a side effect of filling the list");

        // With results but no current hit, "previous" is the last hit and "next" the first.
        hex.FindPreviousCommand.Execute(null);
        hex.CaretOffset.Should().Be(3500);
    }

    [Fact]
    public async Task Text_find_ignores_case_and_reports_no_matches_only_for_a_valid_pattern()
    {
        using var hex = WithNeedles();
        hex.FindQuery = "NEEDLE";
        await hex.RunFindCommand.ExecuteAsync(null);
        hex.SearchResults.Should().HaveCount(3);

        hex.FindQuery = "haystack";
        await hex.RunFindCommand.ExecuteAsync(null);
        hex.SearchResults.Should().BeEmpty();
        hex.FindStatus.Should().Be("no matches");
    }

    // ------------------------------------------------------------------ hex patterns are exact

    [Fact]
    public async Task A_hex_pattern_matches_those_bytes_and_not_their_other_case()
    {
        // "MZ" once, and the three case variants that folding would also have reported.
        using var hex = With(Encoding.ASCII.GetBytes("..mz..Mz..mZ..MZ.."));
        hex.FindKind = HexSearchKind.Hex;
        hex.FindQuery = "4D 5A";

        await hex.RunFindCommand.ExecuteAsync(null);

        hex.SearchResults.Should().ContainSingle().Which.Offset.Should().Be(14);
        hex.FindStatus.Should().Be("1 of 1");
    }

    [Fact]
    public async Task The_find_dialog_keeps_hex_exact_even_with_case_sensitivity_unticked()
    {
        using var hex = With(Encoding.ASCII.GetBytes("..mz..MZ.."));
        var dialog = new FindDialogViewModel(hex) { Mode = HexSearchKind.Hex, Query = "4D5A", CaseSensitive = false };

        await dialog.RunCommand.ExecuteAsync(null);

        hex.SearchResults.Should().ContainSingle().Which.Offset.Should().Be(6);
        dialog.StatusLine.Should().Be("1 match.");
        hex.CaretOffset.Should().Be(6, "the dialog lands on the first hit in the file");
    }

    // ------------------------------------------------------------------ invalid patterns

    [Theory]
    [InlineData(HexSearchKind.Regex, "([a-z")]
    [InlineData(HexSearchKind.Regex, "a{2,1}")]
    [InlineData(HexSearchKind.Regex, "*oops")]
    [InlineData(HexSearchKind.Hex, "4D5")]
    [InlineData(HexSearchKind.Hex, "4D 5G")]
    [InlineData(HexSearchKind.Hex, "0x4D")]
    [InlineData(HexSearchKind.Hex, "- : ,")]
    public async Task A_pattern_that_cannot_be_searched_is_called_invalid_not_absent(HexSearchKind kind, string query)
    {
        HexViewModel.ValidateFindQuery(kind, query).Should().NotBeNull();

        using var hex = WithNeedles();
        hex.FindKind = kind;
        hex.FindQuery = query;
        await hex.RunFindCommand.ExecuteAsync(null);
        hex.FindStatus.Should().Be("invalid pattern");
        hex.SearchResults.Should().BeEmpty();

        var dialog = new FindDialogViewModel(hex) { Mode = kind, Query = query };
        await dialog.RunCommand.ExecuteAsync(null);
        dialog.StatusLine.Should().StartWith("Invalid pattern.");
        dialog.IsSearching.Should().BeFalse();
    }

    [Theory]
    [InlineData(HexSearchKind.Regex, "ne+dle")]
    [InlineData(HexSearchKind.Hex, "6E 65 65 64")]
    [InlineData(HexSearchKind.Hex, "6e:65-65,64")]
    [InlineData(HexSearchKind.Hex, "6E656564")]
    [InlineData(HexSearchKind.Ascii, "([a-z")]
    [InlineData(HexSearchKind.Utf16Le, "4D5")]
    public async Task Valid_patterns_are_searched(HexSearchKind kind, string query)
    {
        HexViewModel.ValidateFindQuery(kind, query).Should().BeNull();

        using var hex = WithNeedles();
        hex.FindKind = kind;
        hex.FindQuery = query;
        await hex.RunFindCommand.ExecuteAsync(null);

        hex.FindStatus.Should().NotBe("invalid pattern").And.NotBe("search failed");
    }

    [Fact]
    public async Task A_regex_that_times_out_is_reported_and_does_not_escape_either_command()
    {
        // Catastrophic backtracking: the 2-second match timeout in HexSearch fires.
        var bytes = Encoding.ASCII.GetBytes(new string('a', 4000) + "!");
        using var hex = With(bytes);

        var dialog = new FindDialogViewModel(hex) { Mode = HexSearchKind.Regex, Query = "(a+)+$" };
        var run = () => dialog.RunCommand.ExecuteAsync(null);
        await run.Should().NotThrowAsync("an exception leaving an async command is rethrown on the UI thread");
        dialog.StatusLine.Should().Contain("took too long");
        dialog.IsSearching.Should().BeFalse();

        hex.FindKind = HexSearchKind.Regex;
        hex.FindQuery = "(a+)+$";
        await hex.RunFindCommand.ExecuteAsync(null);
        hex.FindStatus.Should().Be("pattern too slow");
    }

    // ------------------------------------------------------------------ stale results

    [Fact]
    public async Task Hits_from_a_cancelled_search_are_not_added()
    {
        using var hex = WithNeedles();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var search = () => hex.SearchAsync(new HexSearchOptions(HexSearchKind.Ascii, "needle"), cts.Token);

        await search.Should().ThrowAsync<OperationCanceledException>();
        hex.SearchResults.Should().BeEmpty();
    }

    [Fact]
    public void The_find_command_can_be_restarted_while_a_search_is_running()
    {
        using var hex = WithNeedles();
        hex.RunFindCommand.Should().BeAssignableTo<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>();

        // An AsyncRelayCommand that forbids concurrent execution reports CanExecute == false
        // while its task runs, which made Enter on a corrected query a no-op. Model "still
        // running" with a search that cannot finish until we let it.
        var gate = new ManualResetEventSlim();
        hex.Buffer = new GatedBuffer(gate);
        hex.FindQuery = "x";
        var first = hex.RunFindCommand.ExecuteAsync(null);
        try
        {
            first.IsCompleted.Should().BeFalse();
            hex.RunFindCommand.CanExecute(null).Should().BeTrue();
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>A buffer whose reads block until released, to hold a search open.</summary>
    private sealed class GatedBuffer(ManualResetEventSlim gate) : IHexBuffer
    {
        public long Length => 64;
        public string DisplayName => "gated";
        public bool IsReadOnly => true;

        public int Read(long offset, Span<byte> destination)
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            var n = (int)Math.Min(destination.Length, Math.Max(0, Length - offset));
            destination[..n].Clear();
            return n;
        }

        public void Dispose()
        {
        }
    }

    // ------------------------------------------------------------------ opening files

    [Fact]
    public void A_file_that_cannot_be_opened_leaves_the_tab_exactly_as_it_was()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cinder-hex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var good = Path.Combine(dir, "good.bin");
        var locked = Path.Combine(dir, "locked.bin");
        File.WriteAllBytes(good, Encoding.ASCII.GetBytes("good file"));
        File.WriteAllBytes(locked, Encoding.ASCII.GetBytes("locked file"));
        try
        {
            using var hex = new HexViewModel();

            // Nothing open yet: a failed open leaves the tab empty and reusable, with no path
            // that a later "is this file already open?" check could match.
            hex.Invoking(h => h.OpenFile(dir)).Should().Throw<Exception>("a directory is not a file");
            hex.Buffer.Should().BeNull();
            hex.SourcePath.Should().BeNull();

            hex.OpenFile(good);
            var buffer = hex.Buffer;
            buffer.Should().NotBeNull();
            hex.SourcePath.Should().Be(good);
            hex.CaretOffset = 4;

            // In use by another process (here: another handle that denies sharing).
            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                hex.Invoking(h => h.OpenFile(locked)).Should().Throw<IOException>();
            }
            hex.Buffer.Should().BeSameAs(buffer, "the file that was open is still open");
            hex.SourcePath.Should().Be(good);
            hex.CaretOffset.Should().Be(4);
            hex.CaretByteAscii.Should().Be("' '", "the old buffer is still readable, not disposed");

            hex.Invoking(h => h.OpenFile(Path.Combine(dir, "missing.bin"))).Should().Throw<FileNotFoundException>();
            hex.Buffer.Should().BeSameAs(buffer);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
