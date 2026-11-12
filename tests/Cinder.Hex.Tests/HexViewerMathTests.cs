using Cinder.Hex;
using FluentAssertions;
using Xunit;

namespace Cinder.Hex.Tests;

/// <summary>
/// The hex control is custom-drawn and this project has no headless Avalonia host, so the
/// arithmetic the control's input handlers run lives in <see cref="HexViewerMath"/> and is
/// tested here directly. Each group pins one boundary the control used to get wrong.
/// </summary>
public sealed class HexViewerMathTests
{
    private const long FiveGiB = 5L << 30;

    // ---- scroll clamp: rows stay on the grid at end-of-file ------------------------------------

    [Theory]
    [InlineData(1001, 16)]     // odd length: the old bound (length - 16 = 985) was 9 bytes off the grid
    [InlineData(1000, 16)]
    [InlineData(1024, 16)]     // exact multiple
    [InlineData(1, 16)]
    [InlineData(17, 16)]
    [InlineData(1001, 32)]
    [InlineData(FiveGiB + 7, 16)]
    public void Scrolling_to_the_end_lands_on_the_row_that_holds_the_last_byte(long length, int bytesPerRow)
    {
        var atEnd = HexViewerMath.ClampScrollOffset(long.MaxValue, length, bytesPerRow);

        (atEnd % bytesPerRow).Should().Be(0, "a scroll offset is always the start of a row");
        atEnd.Should().BeLessThanOrEqualTo(length - 1);
        (length - 1 - atEnd).Should().BeLessThan(bytesPerRow, "the last byte is in the first visible row");
        atEnd.Should().Be(HexViewerMath.MaxScrollOffset(length, bytesPerRow));
    }

    [Fact]
    public void Wheeling_to_the_end_and_back_up_never_leaves_the_row_grid()
    {
        const long length = 1001;
        const int bpr = 16;
        long offset = 0;

        // Three rows per wheel notch, down past the end and back to the top.
        for (int i = 0; i < 40; i++)
        {
            offset = HexViewerMath.ClampScrollOffset(offset + 3 * bpr, length, bpr);
            (offset % bpr).Should().Be(0);
        }
        offset.Should().Be(992);
        for (int i = 0; i < 40; i++)
        {
            offset = HexViewerMath.ClampScrollOffset(offset - 3 * bpr, length, bpr);
            (offset % bpr).Should().Be(0);
        }
        offset.Should().Be(0);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(long.MinValue, 0)]
    [InlineData(0, 0)]
    [InlineData(15, 0)]
    [InlineData(16, 16)]
    [InlineData(985, 976)]      // an off-grid request snaps down to its row
    [InlineData(5000, 992)]
    public void Scroll_requests_are_clamped_and_snapped(long requested, long expected)
        => HexViewerMath.ClampScrollOffset(requested, 1001, 16).Should().Be(expected);

    [Theory]
    [InlineData(0, 16)]
    [InlineData(-5, 16)]
    [InlineData(100, 0)]
    public void An_empty_buffer_or_a_zero_row_size_scrolls_nowhere(long length, int bytesPerRow)
    {
        HexViewerMath.ClampScrollOffset(1234, length, bytesPerRow).Should().Be(0);
        HexViewerMath.MaxScrollOffset(length, bytesPerRow).Should().Be(0);
    }

    // ---- caret: never outside the buffer, never -1 ----------------------------------------------

    [Theory]
    [InlineData(1)]      // Right
    [InlineData(16)]     // Down
    [InlineData(-1)]     // Left
    [InlineData(-16)]    // Up
    public void Arrow_keys_on_an_empty_file_keep_the_caret_at_zero(long delta)
    {
        // Math.Min(length - 1, caret + 1) gave -1 here; the view model then showed the offset
        // as 0xFFFFFFFFFFFFFFFF and would bookmark it.
        HexViewerMath.MoveCaret(0, delta, length: 0).Should().Be(0);
    }

    [Fact]
    public void The_caret_stops_at_both_ends_of_the_buffer()
    {
        HexViewerMath.MoveCaret(0, -1, 100).Should().Be(0);
        HexViewerMath.MoveCaret(5, -16, 100).Should().Be(0);
        HexViewerMath.MoveCaret(99, 1, 100).Should().Be(99);
        HexViewerMath.MoveCaret(90, 16, 100).Should().Be(99);
        HexViewerMath.MoveCaret(42, 1, 100).Should().Be(43);
        HexViewerMath.MoveCaret(FiveGiB, 16, FiveGiB + 100).Should().Be(FiveGiB + 16);
        HexViewerMath.MoveCaret(long.MaxValue - 1, 16, long.MaxValue).Should().Be(long.MaxValue - 1, "no overflow at the top of the range");
        HexViewerMath.MoveCaret(-7, 1, 100).Should().Be(0, "a caret pushed in from a binding below zero is pulled back in");
    }

    // ---- selection: the anchor stays put --------------------------------------------------------

    /// <summary>Replays what the control does for each Shift+arrow or drag step.</summary>
    private static (long Start, long End, long Anchor) Extend(long storedAnchor, long start, long end, long caret, long pivot)
    {
        var anchor = HexViewerMath.ResolveAnchor(storedAnchor, start, end, caret);
        var (s, e) = HexViewerMath.SelectionBetween(anchor, pivot);
        return (s, e, anchor);
    }

    [Fact]
    public void Extending_backwards_step_by_step_keeps_the_far_end_of_the_selection()
    {
        // Click byte 100, then Shift+Left three times. The old code swapped the ends on each
        // step and produced [99,100], [98,99], [97,98]: always two bytes, the anchor lost.
        long start = 100, end = 100, caret = 100, anchor = 100;
        foreach (var pivot in new long[] { 99, 98, 97 })
        {
            (start, end, anchor) = Extend(anchor, start, end, caret, pivot);
            caret = pivot;
            end.Should().Be(100);
            start.Should().Be(pivot);
        }
        (start, end).Should().Be((97L, 100L));
    }

    [Fact]
    public void Dragging_back_and_forth_across_the_anchor_always_selects_anchor_to_pointer()
    {
        long start = 100, end = 100, caret = 100, anchor = 100;
        foreach (var pivot in new long[] { 90, 60, 95, 100, 130, 110, 40 })
        {
            (start, end, anchor) = Extend(anchor, start, end, caret, pivot);
            caret = pivot;
            (start, end).Should().Be((Math.Min(100, pivot), Math.Max(100, pivot)));
        }
    }

    [Fact]
    public void With_no_selection_the_caret_is_the_anchor()
    {
        var (start, end, anchor) = Extend(storedAnchor: -1, start: -1, end: -1, caret: 50, pivot: 34);
        (start, end, anchor).Should().Be((34L, 50L, 50L));
    }

    [Fact]
    public void A_selection_set_from_outside_is_extended_from_the_end_the_caret_is_not_on()
    {
        // A search hit selects [200, 207] with the caret on 200; the control never saw an
        // anchor for it (its stored anchor is stale, from an earlier click at 10).
        var (start, end, anchor) = Extend(storedAnchor: 10, start: 200, end: 207, caret: 200, pivot: 190);
        (start, end, anchor).Should().Be((190L, 207L, 207L));

        (start, end, anchor) = Extend(storedAnchor: 10, start: 200, end: 207, caret: 207, pivot: 220);
        (start, end, anchor).Should().Be((200L, 220L, 200L));
    }

    [Fact]
    public void Selections_work_above_four_gibibytes()
    {
        var (start, end, _) = Extend(FiveGiB, FiveGiB, FiveGiB, FiveGiB, FiveGiB - 3);
        (start, end).Should().Be((FiveGiB - 3, FiveGiB));
    }

    // ---- text pane hit test ---------------------------------------------------------------------

    [Theory]
    [InlineData(7.8)]    // JetBrains Mono at 13 px
    [InlineData(7.15)]   // Consolas at 13 px
    [InlineData(6.0)]
    [InlineData(12.5)]
    public void A_click_anywhere_inside_a_text_cell_selects_that_cell(double glyphAdvance)
    {
        const double textColumnX = 500;
        for (int cell = 0; cell < 16; cell++)
        {
            var left = textColumnX + cell * glyphAdvance;
            foreach (var fraction in new[] { 0.02, 0.25, 0.5, 0.75, 0.98 })
            {
                // Measured from 6 px left of the glyphs, as before, every probe past the first
                // ~1.8 px of a 7.8 px cell landed on cell + 1.
                HexViewerMath.TextPaneByteIndex(left + fraction * glyphAdvance, textColumnX, glyphAdvance, 16)
                    .Should().Be(cell, $"x is {fraction:P0} of the way across cell {cell}");
            }
        }
    }

    [Fact]
    public void Clicks_in_the_gutter_before_the_first_glyph_and_past_the_last_are_clamped()
    {
        HexViewerMath.TextPaneByteIndex(494, 500, 7.8, 16).Should().Be(0, "the 6 px before the first glyph belongs to byte 0");
        HexViewerMath.TextPaneByteIndex(499.9, 500, 7.8, 16).Should().Be(0);
        HexViewerMath.TextPaneByteIndex(500 + 16 * 7.8 + 40, 500, 7.8, 16).Should().Be(15);
        HexViewerMath.TextPaneByteIndex(double.MaxValue, 500, 7.8, 16).Should().Be(15);
        HexViewerMath.TextPaneByteIndex(double.NaN, 500, 7.8, 16).Should().Be(0);
        HexViewerMath.TextPaneByteIndex(510, 500, 0, 16).Should().Be(0);
    }

    // ---- caret row -------------------------------------------------------------------------------

    [Fact]
    public void The_caret_row_is_the_row_that_contains_the_caret_byte()
    {
        HexViewerMath.RowContains(976, 16, 976).Should().BeTrue();
        HexViewerMath.RowContains(976, 16, 991).Should().BeTrue();
        HexViewerMath.RowContains(976, 16, 992).Should().BeFalse();
        HexViewerMath.RowContains(976, 16, 975).Should().BeFalse();

        // Off the grid (a scroll offset bound in from outside): integer division said the
        // caret at 976 was in the row starting at 984, because both divide to row 61.
        HexViewerMath.RowContains(984, 16, 976).Should().BeFalse();
        HexViewerMath.RowContains(984, 16, 990).Should().BeTrue();
        HexViewerMath.RowContains(0, 16, -1).Should().BeFalse();
    }

    // ---- buffers ---------------------------------------------------------------------------------

    [Fact]
    public void Buffers_return_nothing_for_a_negative_offset_instead_of_reading_before_their_start()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        Span<byte> dest = stackalloc byte[4];

        using var array = new ByteArrayHexBuffer(bytes);
        array.Read(-1, dest).Should().Be(0);

        var path = Path.Combine(Path.GetTempPath(), "cinder-hex-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, bytes);
        try
        {
            using var mapped = new MmapHexBuffer(path);
            mapped.Read(-1, dest).Should().Be(0);
            mapped.Read(long.MinValue, dest).Should().Be(0);
            mapped.Read(2, dest).Should().Be(2);
            dest[0].Should().Be(3);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
