using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Cinder.Hex;
using FluentAssertions;
using Xunit;

namespace Cinder.Hex.Tests;

/// <summary>
/// The real <see cref="HexViewer"/>, hosted in a headless window and driven the way a user
/// drives it: key presses, pointer presses, drags and wheel notches go through Avalonia's
/// input pipeline to the control's own handlers, and the assertions read back the same
/// properties the view model binds to. <see cref="HexViewerMathTests"/> covers the arithmetic;
/// these cover the wiring.
/// </summary>
public sealed class HexViewerControlTests
{
    private const long FourGiB = 1L << 32;

    // ------------------------------------------------------------------- hosting ----

    private static (Window Window, HexViewer Viewer) Host(IHexBuffer? buffer, bool utf16 = false, double height = 520)
    {
        var viewer = new HexViewer { Buffer = buffer, TextIsUtf16 = utf16 };
        var window = new Window { Width = 1100, Height = height, Content = viewer };
        window.Show();
        Settle();
        viewer.Focus();
        viewer.Bounds.Height.Should().BeGreaterThan(100, "the control must be laid out for input to reach it");
        return (window, viewer);
    }

    /// <summary>Runs pending layout and dispatcher work, then a render pass (which calls the control's Render).</summary>
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static ByteArrayHexBuffer Bytes(int length)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++)
        {
            b[i] = (byte)(i * 7 + 1);
        }
        return new ByteArrayHexBuffer(b);
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            window.KeyPressQwerty(key, modifiers);
            window.KeyReleaseQwerty(key, modifiers);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Centre of the cell the control draws for <paramref name="offset"/>, in window coordinates.</summary>
    private static Point CellCentre(Window window, HexViewer viewer, long offset, bool textPane)
    {
        var bounds = viewer.GetCellBounds(offset, textPane);
        bounds.Should().NotBeNull($"byte {offset} must be on a visible row");
        var inWindow = viewer.TranslatePoint(bounds!.Value.Center, window);
        inWindow.Should().NotBeNull();
        return inWindow!.Value;
    }

    private static void Click(Window window, Point at, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseDown(at, MouseButton.Left, modifiers);
        window.MouseUp(at, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Wheel(Window window, HexViewer viewer, double notches)
    {
        var at = viewer.TranslatePoint(new Point(viewer.Bounds.Width / 2, viewer.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(at, new Vector(0, notches));
        Dispatcher.UIThread.RunJobs();
    }

    // ------------------------------------------------------------- keyboard selection ----

    [AvaloniaFact]
    public void Shift_left_three_times_selects_the_three_bytes_before_the_caret_through_to_it()
    {
        var (window, viewer) = Host(Bytes(4096));
        viewer.CaretOffset = 100;

        Press(window, PhysicalKey.ArrowLeft, RawInputModifiers.Shift, times: 3);

        // Before the anchor was tracked this was [97, 98]: each step swapped the two ends.
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((97L, 100L));
        viewer.CaretOffset.Should().Be(97);

        // Back across the anchor and beyond: the anchor byte stays in the selection.
        Press(window, PhysicalKey.ArrowRight, RawInputModifiers.Shift, times: 5);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((100L, 102L));
        viewer.CaretOffset.Should().Be(102);

        // By rows as well.
        Press(window, PhysicalKey.ArrowUp, RawInputModifiers.Shift, times: 2);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((70L, 100L));

        // An unmodified arrow drops the selection.
        Press(window, PhysicalKey.ArrowRight);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((-1L, -1L));
        viewer.CaretOffset.Should().Be(71);
    }

    // ------------------------------------------------------------------ pointer drag ----

    [AvaloniaFact]
    public void Dragging_backwards_keeps_the_byte_the_drag_started_on()
    {
        var (window, viewer) = Host(Bytes(4096));

        window.MouseDown(CellCentre(window, viewer, 40, textPane: false), MouseButton.Left);
        foreach (var over in new long[] { 39, 38, 33, 20, 5 })
        {
            window.MouseMove(CellCentre(window, viewer, over, textPane: false), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((over, 40L), $"dragged back to byte {over}");
        }

        // Still the same drag: forwards past the anchor, then back again.
        window.MouseMove(CellCentre(window, viewer, 58, textPane: false), RawInputModifiers.LeftMouseButton);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((40L, 58L));
        window.MouseMove(CellCentre(window, viewer, 12, textPane: true), RawInputModifiers.LeftMouseButton);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((12L, 40L), "the text pane addresses the same bytes");

        window.MouseUp(CellCentre(window, viewer, 12, textPane: true), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((12L, 40L));
        viewer.CaretOffset.Should().Be(12);

        // Releasing ends the drag: moving the pointer no longer changes the selection.
        window.MouseMove(CellCentre(window, viewer, 70, textPane: false));
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((12L, 40L));
    }

    [AvaloniaFact]
    public void Shift_click_extends_from_the_original_click_in_either_direction()
    {
        var (window, viewer) = Host(Bytes(4096));

        Click(window, CellCentre(window, viewer, 64, textPane: false));
        (viewer.SelectionStart, viewer.SelectionEnd, viewer.CaretOffset).Should().Be((64L, 64L, 64L));

        Click(window, CellCentre(window, viewer, 30, textPane: false), RawInputModifiers.Shift);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((30L, 64L));

        Click(window, CellCentre(window, viewer, 90, textPane: false), RawInputModifiers.Shift);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((64L, 90L));
    }

    // -------------------------------------------------------------------- hit testing ----

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_click_at_the_centre_of_a_text_pane_cell_selects_that_byte(bool utf16)
    {
        var (window, viewer) = Host(Bytes(4096), utf16);

        // Every column of the third row, and the first and last cells of a few others.
        var targets = Enumerable.Range(32, 16).Select(i => (long)i).Concat([0, 15, 16, 31, 160, 175]);
        foreach (var offset in targets)
        {
            Click(window, CellCentre(window, viewer, offset, textPane: true));

            // Measured from 6 px left of the glyphs, this landed on offset + 1 for every cell
            // but the last of each row.
            viewer.CaretOffset.Should().Be(offset, $"the pointer was on the glyph of byte {offset}");
            (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((offset, offset));
        }
    }

    [AvaloniaFact]
    public void A_click_near_either_edge_of_a_text_cell_still_selects_that_cell()
    {
        var (window, viewer) = Host(Bytes(4096));
        foreach (var offset in new long[] { 48, 53, 63 })
        {
            var cell = viewer.GetCellBounds(offset, textPane: true)!.Value;
            foreach (var fraction in new[] { 0.1, 0.9 })
            {
                var at = viewer.TranslatePoint(new Point(cell.X + cell.Width * fraction, cell.Center.Y), window)!.Value;
                Click(window, at);
                viewer.CaretOffset.Should().Be(offset, $"{fraction:P0} of the way across the cell of byte {offset}");
            }
        }
    }

    [AvaloniaFact]
    public void A_click_at_the_centre_of_a_hex_cell_selects_that_byte()
    {
        var (window, viewer) = Host(Bytes(4096));
        foreach (var offset in new long[] { 0, 7, 8, 15, 16, 39, 40, 100, 111 })   // both sides of the 8-byte group gap
        {
            Click(window, CellCentre(window, viewer, offset, textPane: false));
            viewer.CaretOffset.Should().Be(offset);
        }
    }

    // ------------------------------------------------------------- scrolling to the end ----

    [AvaloniaTheory]
    [InlineData(1001)]
    [InlineData(1000)]
    [InlineData(4099)]
    [InlineData(1024)]
    public void Page_down_and_the_wheel_stop_on_a_row_boundary_at_the_end_of_the_file(int length)
    {
        var (window, viewer) = Host(Bytes(length));
        var lastRow = (length - 1L) / 16 * 16;

        Press(window, PhysicalKey.PageDown, times: 20);
        // The old bound was length - 16, a row start only when the length is a multiple of 16.
        viewer.ScrollOffset.Should().Be(lastRow);

        // Back up a little: still on the grid, not shifted by length % 16.
        Wheel(window, viewer, notches: 1);
        viewer.ScrollOffset.Should().Be(lastRow - 3 * 16);
        Press(window, PhysicalKey.PageUp);
        (viewer.ScrollOffset % 16).Should().Be(0);

        // And down again with the wheel alone.
        viewer.ScrollOffset = 0;
        for (int i = 0; i < 200; i++)
        {
            Wheel(window, viewer, notches: -1);
            (viewer.ScrollOffset % 16).Should().Be(0);
        }
        viewer.ScrollOffset.Should().Be(lastRow);

        // The scroll bar path (ILogicalScrollable) agrees.
        var scrollable = (ILogicalScrollable)viewer;
        scrollable.Offset = new Vector(0, scrollable.Extent.Height);
        viewer.ScrollOffset.Should().Be(lastRow);

        // The last byte is on screen and clickable at the end position.
        Settle();
        Click(window, CellCentre(window, viewer, length - 1, textPane: false));
        viewer.CaretOffset.Should().Be(length - 1);
    }

    // -------------------------------------------------------------------- empty buffer ----

    [AvaloniaFact]
    public void Arrow_keys_on_an_empty_buffer_leave_the_caret_at_zero()
    {
        var (window, viewer) = Host(new ByteArrayHexBuffer([]));

        foreach (var key in new[] { PhysicalKey.ArrowRight, PhysicalKey.ArrowDown, PhysicalKey.ArrowLeft, PhysicalKey.ArrowUp, PhysicalKey.PageDown, PhysicalKey.PageUp })
        {
            Press(window, key);
            viewer.CaretOffset.Should().Be(0, $"{key} has nowhere to go in an empty buffer (it used to produce -1)");
            viewer.ScrollOffset.Should().Be(0);

            Press(window, key, RawInputModifiers.Shift);
            viewer.CaretOffset.Should().Be(0);
            (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((-1L, -1L), "there is no byte to select");
        }

        Press(window, PhysicalKey.End, RawInputModifiers.Control);
        viewer.CaretOffset.Should().Be(0);

        // A click anywhere selects nothing and moves nothing.
        Click(window, new Point(300, 200));
        viewer.CaretOffset.Should().Be(0);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((-1L, -1L));
        Settle();
    }

    [AvaloniaFact]
    public void A_control_with_no_buffer_ignores_input()
    {
        var (window, viewer) = Host(buffer: null);
        Press(window, PhysicalKey.ArrowRight);
        Press(window, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
        Click(window, new Point(300, 200));
        Wheel(window, viewer, -1);
        Settle();
        (viewer.CaretOffset, viewer.ScrollOffset, viewer.SelectionStart).Should().Be((0L, 0L, -1L));
    }

    // ------------------------------------------------------ first byte and last byte ----

    [AvaloniaFact]
    public void Caret_and_selection_stop_at_the_first_byte()
    {
        var (window, viewer) = Host(Bytes(1001));
        viewer.CaretOffset = 2;

        Press(window, PhysicalKey.ArrowLeft, times: 5);
        viewer.CaretOffset.Should().Be(0);
        Press(window, PhysicalKey.ArrowUp);
        viewer.CaretOffset.Should().Be(0);

        Press(window, PhysicalKey.ArrowLeft, RawInputModifiers.Shift, times: 3);
        (viewer.SelectionStart, viewer.SelectionEnd, viewer.CaretOffset).Should().Be((0L, 0L, 0L), "extending left from byte 0 selects byte 0 and nothing before it");

        Press(window, PhysicalKey.ArrowRight, RawInputModifiers.Shift, times: 2);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((0L, 2L));
        Press(window, PhysicalKey.ArrowUp, RawInputModifiers.Shift);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((0L, 0L), "up from the first row goes to byte 0");
        viewer.ScrollOffset.Should().Be(0);
    }

    [AvaloniaFact]
    public void Caret_and_selection_stop_at_the_last_byte()
    {
        const int length = 1001;                    // last byte 1000, alone on the row starting at 992
        var (window, viewer) = Host(Bytes(length));

        Press(window, PhysicalKey.End, RawInputModifiers.Control);
        viewer.CaretOffset.Should().Be(length - 1);
        (viewer.ScrollOffset % 16).Should().Be(0);
        viewer.GetCellBounds(length - 1, textPane: false).Should().NotBeNull("Ctrl+End must bring the last byte into view");

        Press(window, PhysicalKey.ArrowRight, times: 3);
        viewer.CaretOffset.Should().Be(length - 1);
        Press(window, PhysicalKey.ArrowDown);
        viewer.CaretOffset.Should().Be(length - 1);

        Press(window, PhysicalKey.ArrowRight, RawInputModifiers.Shift, times: 2);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((length - 1L, length - 1L));
        Press(window, PhysicalKey.ArrowLeft, RawInputModifiers.Shift, times: 2);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((length - 3L, length - 1L));
        Press(window, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((length - 1L, length - 1L), "down from the last row goes to the last byte");

        // A click in the empty part of the last row, or below it, lands on the last byte.
        Settle();
        var lastCell = viewer.GetCellBounds(length - 1, textPane: false)!.Value;
        Click(window, viewer.TranslatePoint(new Point(lastCell.Right + 80, lastCell.Center.Y), window)!.Value);
        viewer.CaretOffset.Should().Be(length - 1);
        Click(window, viewer.TranslatePoint(new Point(lastCell.Center.X, lastCell.Bottom + 60), window)!.Value);
        viewer.CaretOffset.Should().Be(length - 1);

        Press(window, PhysicalKey.Home, RawInputModifiers.Control);
        (viewer.CaretOffset, viewer.ScrollOffset).Should().Be((0L, 0L));
    }

    // ------------------------------------------------------------------ beyond 4 GiB ----

    [AvaloniaFact]
    public void A_buffer_over_four_gibibytes_shows_sixteen_digit_offsets_and_reaches_its_end()
    {
        const long length = 5 * (1L << 30) + 5;     // 0x1_4000_0005: last byte alone on its row
        var buffer = new FakeLargeBuffer(length);
        var (window, viewer) = Host(buffer);
        var lastRow = (length - 1) / 16 * 16;

        viewer.FormatRowOffset(0).Should().Be("0000000000000000");
        viewer.FormatRowOffset(lastRow).Should().Be("0000000140000000");
        viewer.RowCount.Should().Be(length / 16 + 1);

        buffer.ResetReads();
        Press(window, PhysicalKey.End, RawInputModifiers.Control);
        viewer.CaretOffset.Should().Be(length - 1);
        viewer.ScrollOffset.Should().Be(lastRow, "no int arithmetic on the way to the end");

        Settle();
        buffer.LowestReadOffset.Should().Be(lastRow, "the render pass asked the buffer for the last row");
        viewer.GetCellBounds(length - 1, textPane: true).Should().NotBeNull();

        // Selection and pointer input work up there too.
        Press(window, PhysicalKey.ArrowLeft, RawInputModifiers.Shift, times: 3);
        (viewer.SelectionStart, viewer.SelectionEnd).Should().Be((length - 4, length - 1));
        Press(window, PhysicalKey.PageUp);
        viewer.ScrollOffset.Should().Be(lastRow - 32 * 16);
        Settle();
        var target = viewer.ScrollOffset + 16 * 3 + 9;
        Click(window, CellCentre(window, viewer, target, textPane: true));
        viewer.CaretOffset.Should().Be(target);
        target.Should().BeGreaterThan(FourGiB);

        // Scroll bar to the bottom, wheel past it: clamped to the last row.
        var scrollable = (ILogicalScrollable)viewer;
        scrollable.Offset = new Vector(0, scrollable.Extent.Height);
        viewer.ScrollOffset.Should().Be(lastRow);
        Wheel(window, viewer, notches: -5);
        viewer.ScrollOffset.Should().Be(lastRow);

        // A jump from a binding into the middle renders from there.
        viewer.ScrollOffset = FourGiB;
        buffer.ResetReads();
        Settle();
        buffer.LowestReadOffset.Should().Be(FourGiB);
    }

    [AvaloniaTheory]
    [InlineData(4096L, 8)]
    [InlineData(FourGiB - 1, 8)]
    [InlineData(FourGiB + 1, 16)]
    public void The_offset_gutter_widens_only_when_offsets_no_longer_fit_in_eight_digits(long length, int digits)
    {
        var (_, viewer) = Host(new FakeLargeBuffer(length));
        viewer.FormatRowOffset(0).Should().HaveLength(digits);
        viewer.FormatRowOffset((length - 1) / 16 * 16).Should().HaveLength(digits);
    }

    /// <summary>
    /// A buffer of any length with no storage: byte <c>n</c> is <c>(byte)n</c>. Stands in for a
    /// multi-gigabyte image and records where it was read.
    /// </summary>
    private sealed class FakeLargeBuffer(long length) : IHexBuffer
    {
        public long Length => length;
        public string DisplayName => "(fake large buffer)";
        public bool IsReadOnly => true;
        public long LowestReadOffset { get; private set; } = long.MaxValue;

        public void ResetReads() => LowestReadOffset = long.MaxValue;

        public int Read(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset >= length)
            {
                return 0;
            }
            LowestReadOffset = Math.Min(LowestReadOffset, offset);
            var n = (int)Math.Min(destination.Length, length - offset);
            for (int i = 0; i < n; i++)
            {
                destination[i] = (byte)(offset + i);
            }
            return n;
        }

        public void Dispose() { }
    }
}
