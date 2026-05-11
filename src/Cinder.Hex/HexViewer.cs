using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Cinder.Hex;

/// <summary>
/// Cinder's custom virtualized hex viewer.
///
/// Layout per row: <c>OFFSET (16 hex digits) │ HEX bytes │ ASCII gutter │ UTF-16LE gutter</c>.
///
/// Only the visible rows are rendered, so opening a 100 GB image is constant-time. Reads come
/// from <see cref="IHexBuffer"/>; in production that's an mmap-backed buffer so paging is the
/// kernel's job.
///
/// Implements <see cref="ILogicalScrollable"/> so a wrapping <see cref="ScrollViewer"/> can
/// drive scrolling against the buffer's full extent without forcing the control to actually
/// allocate that much pixel space.
/// </summary>
public sealed class HexViewer : Control, ILogicalScrollable
{
    public static readonly StyledProperty<IHexBuffer?> BufferProperty =
        AvaloniaProperty.Register<HexViewer, IHexBuffer?>(nameof(Buffer));

    public static readonly StyledProperty<long> ScrollOffsetProperty =
        AvaloniaProperty.Register<HexViewer, long>(nameof(ScrollOffset));

    public static readonly StyledProperty<int> BytesPerRowProperty =
        AvaloniaProperty.Register<HexViewer, int>(nameof(BytesPerRow), 16);

    public static readonly StyledProperty<long> CaretOffsetProperty =
        AvaloniaProperty.Register<HexViewer, long>(nameof(CaretOffset));

    public static readonly StyledProperty<double> CinderFontSizeProperty =
        AvaloniaProperty.Register<HexViewer, double>(nameof(CinderFontSize), 13.0);

    public static readonly StyledProperty<IBrush?> RowBrushProperty =
        AvaloniaProperty.Register<HexViewer, IBrush?>(nameof(RowBrush), Brushes.Transparent);

    public static readonly StyledProperty<long> SelectionStartProperty =
        AvaloniaProperty.Register<HexViewer, long>(nameof(SelectionStart), defaultValue: -1);

    public static readonly StyledProperty<long> SelectionEndProperty =
        AvaloniaProperty.Register<HexViewer, long>(nameof(SelectionEnd), defaultValue: -1);

    public long SelectionStart
    {
        get => GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public long SelectionEnd
    {
        get => GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    public IHexBuffer? Buffer
    {
        get => GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
    }

    public long ScrollOffset
    {
        get => GetValue(ScrollOffsetProperty);
        set => SetValue(ScrollOffsetProperty, value);
    }

    public int BytesPerRow
    {
        get => GetValue(BytesPerRowProperty);
        set => SetValue(BytesPerRowProperty, value);
    }

    public long CaretOffset
    {
        get => GetValue(CaretOffsetProperty);
        set => SetValue(CaretOffsetProperty, value);
    }

    public double CinderFontSize
    {
        get => GetValue(CinderFontSizeProperty);
        set => SetValue(CinderFontSizeProperty, value);
    }

    public IBrush? RowBrush
    {
        get => GetValue(RowBrushProperty);
        set => SetValue(RowBrushProperty, value);
    }

    public IList<HexSearchHit> Highlights { get; } = new List<HexSearchHit>();
    public IList<StructureOverlay> Overlays { get; } = new List<StructureOverlay>();

    public static readonly StyledProperty<System.Collections.IEnumerable?> BookmarksProperty =
        AvaloniaProperty.Register<HexViewer, System.Collections.IEnumerable?>(nameof(Bookmarks));

    public System.Collections.IEnumerable? Bookmarks
    {
        get => GetValue(BookmarksProperty);
        set => SetValue(BookmarksProperty, value);
    }

    private readonly Typeface _typeface = new("Cascadia Mono, Consolas, monospace");
    private double _glyphAdvance;
    private double _rowHeight;
    private Size _viewport;

    // Brushes cached once, reused every frame. Render is on the hot path of every wheel tick
    // and scrollbar drag; allocating SolidColorBrush per-paint produces GC churn that's
    // perceptible as scroll jank on multi-MB files.
    private static readonly IBrush BrushFg = Brushes.Gainsboro;
    private static readonly IBrush BrushMuted = new SolidColorBrush(Color.FromRgb(0x9C, 0xA0, 0xAB));
    private static readonly IBrush BrushAccent = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x1A));
    private static readonly IBrush BrushHitBg = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0x7A, 0x1A));
    private static readonly IBrush BrushCaret = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47));
    private static readonly IBrush BrushStripe = new SolidColorBrush(Color.FromArgb(0x14, 0x9C, 0xA0, 0xAB));
    private static readonly IBrush BrushCaretRow = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0x7A, 0x1A));
    private static readonly IBrush BrushSelection = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0x7A, 0x1A));
    private static readonly IBrush BrushBookmark = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xB3, 0x47));

    static HexViewer()
    {
        // Brushes constructed off the UI thread above need to be frozen for cross-thread reuse.
        ((SolidColorBrush)BrushMuted).ToImmutable();
        ((SolidColorBrush)BrushAccent).ToImmutable();
        ((SolidColorBrush)BrushHitBg).ToImmutable();
        ((SolidColorBrush)BrushCaret).ToImmutable();
        ((SolidColorBrush)BrushStripe).ToImmutable();
        ((SolidColorBrush)BrushCaretRow).ToImmutable();
        ((SolidColorBrush)BrushSelection).ToImmutable();
        ((SolidColorBrush)BrushBookmark).ToImmutable();

        // Tell the wrapping ScrollViewer to re-read scroll info when EXTENT changes (buffer
        // swap, BPR change). NOT when ScrollOffset changes — that creates a feedback loop where
        // the ScrollViewer's own thumb-drag setter triggers an invalidation that makes the
        // ScrollViewer re-query offset, which is the cause of the perceptible drag jank.
        BufferProperty.Changed.AddClassHandler<HexViewer>((c, _) => c.RaiseScrollInvalidated(EventArgs.Empty));
        BytesPerRowProperty.Changed.AddClassHandler<HexViewer>((c, _) => c.RaiseScrollInvalidated(EventArgs.Empty));
    }

    public HexViewer()
    {
        Focusable = true;
        ClipToBounds = true;
        AffectsRender<HexViewer>(BufferProperty, ScrollOffsetProperty, BytesPerRowProperty,
            CaretOffsetProperty, CinderFontSizeProperty,
            SelectionStartProperty, SelectionEndProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureGlyph();
        var contentWidth = OffsetColumnChars * _glyphAdvance + 12 +
                           BytesPerRow * 3 * _glyphAdvance + 12 +
                           BytesPerRow * _glyphAdvance + 12 +
                           BytesPerRow * _glyphAdvance + 12;
        // Avalonia rejects ∞ from MeasureOverride. Logical scrolling means the wrapping
        // ScrollViewer hands us its viewport size — we never claim the buffer's full extent.
        var width = double.IsFinite(availableSize.Width)
            ? Math.Max(contentWidth, availableSize.Width)
            : contentWidth;
        var height = double.IsFinite(availableSize.Height)
            ? availableSize.Height
            : Math.Max(_rowHeight * 32, 600);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        if (_viewport != arranged)
        {
            _viewport = arranged;
            RaiseScrollInvalidated(EventArgs.Empty);
        }
        return arranged;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        MeasureGlyph();

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var rowBrush = RowBrush ?? Brushes.Transparent;
        context.FillRectangle(rowBrush, bounds);

        var buffer = Buffer;
        if (buffer is null || buffer.Length == 0)
        {
            return; // The wrapping view shows the friendly empty state.
        }

        var visibleRows = Math.Max(1, (int)Math.Ceiling(bounds.Height / _rowHeight));
        var bytesPerRow = Math.Max(1, BytesPerRow);
        var capacity = visibleRows * bytesPerRow;
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var read = pool.Rent(capacity);
        try
        {
            var bytesRead = buffer.Read(ScrollOffset, read.AsSpan(0, capacity));

            var caretRow = BytesPerRow > 0 ? CaretOffset / BytesPerRow : -1;
            var firstVisibleRow = ScrollOffset / Math.Max(1, BytesPerRow);

            for (int row = 0; row < visibleRows; row++)
            {
                var rowOffset = ScrollOffset + (long)row * bytesPerRow;
                if (rowOffset >= buffer.Length)
                {
                    break;
                }

                var y = row * _rowHeight;
                var absoluteRow = firstVisibleRow + row;

                // Row band — alternate stripes for readability + accent band on caret row.
                if (absoluteRow == caretRow)
                {
                    context.FillRectangle(BrushCaretRow, new Rect(0, y, bounds.Width, _rowHeight));
                    // 2px left border — caret-row affordance per the design system.
                    context.FillRectangle(BrushAccent, new Rect(0, y, 2, _rowHeight));
                }
                else if ((absoluteRow & 1) == 1)
                {
                    context.FillRectangle(BrushStripe, new Rect(0, y, bounds.Width, _rowHeight));
                }

                DrawRow(context, rowOffset, read.AsSpan(row * bytesPerRow, Math.Min(bytesPerRow, Math.Max(0, bytesRead - row * bytesPerRow))),
                    bytesPerRow, y);
            }
        }
        finally
        {
            pool.Return(read);
        }
    }

    private void DrawRow(DrawingContext context, long rowOffset, ReadOnlySpan<byte> row, int bytesPerRow, double y)
    {
        var x = 6.0;

        // Offset column
        var offsetText = rowOffset.ToString("X16", CultureInfo.InvariantCulture);
        DrawText(context, offsetText, new Point(x, y), BrushMuted);
        x += OffsetColumnChars * _glyphAdvance + 12;

        // Highlight any search hits intersecting this row
        DrawRowHighlights(context, rowOffset, bytesPerRow, x, y);

        // Hex column
        var hexX = x;
        for (int i = 0; i < bytesPerRow; i++)
        {
            if (i >= row.Length)
            {
                break;
            }
            var byteText = row[i].ToString("X2", CultureInfo.InvariantCulture);
            var byteOffset = rowOffset + i;
            var brush = byteOffset == CaretOffset ? BrushCaret : (IsBookmarked(byteOffset) ? BrushAccent : BrushFg);
            DrawText(context, byteText, new Point(hexX + i * 3 * _glyphAdvance, y), brush);
        }
        x += bytesPerRow * 3 * _glyphAdvance + 12;

        // ASCII column
        var asciiX = x;
        for (int i = 0; i < row.Length; i++)
        {
            var b = row[i];
            var c = b is >= 0x20 and < 0x7F ? (char)b : '.';
            DrawText(context, c.ToString(), new Point(asciiX + i * _glyphAdvance, y), BrushMuted);
        }
        x += bytesPerRow * _glyphAdvance + 12;

        // UTF-16LE column (one glyph per 2 bytes). Random binary will land all over CJK; only
        // emit characters that are actually informative for a forensic examiner — basic Latin
        // printable + Latin-1 supplement printable. Everything else becomes '·'.
        var utfX = x;
        for (int i = 0; i + 1 < row.Length; i += 2)
        {
            var ch = (char)(row[i] | (row[i + 1] << 8));
            var glyph = IsUsefulUtf16Char(ch) ? ch : '·';
            DrawText(context, glyph.ToString(), new Point(utfX + i * _glyphAdvance / 2, y), BrushMuted);
        }
    }

    private void DrawRowHighlights(DrawingContext context, long rowOffset, int bytesPerRow,
        double hexStartX, double y)
    {
        var rowEnd = rowOffset + bytesPerRow;

        // Search hits (orange)
        foreach (var hit in Highlights)
        {
            var hitEnd = hit.Offset + hit.Length;
            if (hit.Offset >= rowEnd || hitEnd <= rowOffset)
            {
                continue;
            }
            DrawByteBand(context, BrushHitBg, hit.Offset, hitEnd - hit.Offset, rowOffset, bytesPerRow, hexStartX, y);
        }

        // Selection (deeper orange, primary)
        if (SelectionStart >= 0 && SelectionEnd >= SelectionStart)
        {
            var selEnd = SelectionEnd + 1;
            if (SelectionStart < rowEnd && selEnd > rowOffset)
            {
                DrawByteBand(context, BrushSelection, SelectionStart, selEnd - SelectionStart, rowOffset, bytesPerRow, hexStartX, y);
            }
        }

        // Bookmarks (warm yellow band on the offset gutter)
        if (Bookmarks is not null)
        {
            foreach (var item in Bookmarks)
            {
                if (item is Bookmark bm && bm.Offset >= rowOffset && bm.Offset < rowEnd)
                {
                    context.FillRectangle(BrushBookmark, new Rect(0, y, 4, _rowHeight));
                    break;
                }
            }
        }
    }

    private void DrawByteBand(DrawingContext context, IBrush brush, long start, long length,
        long rowOffset, int bytesPerRow, double hexStartX, double y)
    {
        var localStart = (int)Math.Max(0, start - rowOffset);
        var localEnd = (int)Math.Min(bytesPerRow, start + length - rowOffset);
        if (localEnd <= localStart)
        {
            return;
        }
        var rect = new Rect(
            hexStartX + localStart * 3 * _glyphAdvance,
            y,
            (localEnd - localStart) * 3 * _glyphAdvance,
            _rowHeight);
        context.FillRectangle(brush, rect);
    }

    private static bool IsUsefulUtf16Char(char c) =>
        c is >= (char)0x20 and <= (char)0x7E       // basic Latin printable
          or >= (char)0xA1 and <= (char)0xFF       // Latin-1 supplement
          or >= (char)0x100 and <= (char)0x17F     // Latin Extended-A
          or >= (char)0x180 and <= (char)0x24F;    // Latin Extended-B

    private bool IsBookmarked(long offset)
    {
        if (Bookmarks is null)
        {
            return false;
        }
        foreach (var item in Bookmarks)
        {
            if (item is Bookmark bm && bm.Offset == offset)
            {
                return true;
            }
        }
        return false;
    }

    private void DrawText(DrawingContext context, string text, Point origin, IBrush brush)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, CinderFontSize, brush);
        context.DrawText(ft, origin);
    }

    private void DrawCenteredText(DrawingContext context, string text, Rect bounds)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, CinderFontSize, Brushes.DimGray);
        var origin = new Point(bounds.Center.X - ft.Width / 2, bounds.Center.Y - ft.Height / 2);
        context.DrawText(ft, origin);
    }

    private void MeasureGlyph()
    {
        var ft = new FormattedText("00", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, CinderFontSize, Brushes.White);
        _glyphAdvance = ft.Width / 2;
        _rowHeight = ft.Height + 2;
    }

    private const int OffsetColumnChars = 16;

    /// <summary>Compute total row count for the current buffer + bytes-per-row.</summary>
    public long RowCount => Buffer is null ? 0 : (Buffer.Length + BytesPerRow - 1) / BytesPerRow;

    public void EnsureCaretVisible()
    {
        var buffer = Buffer;
        if (buffer is null || _rowHeight <= 0)
        {
            return;
        }
        var visibleRows = Math.Max(1, (int)(Bounds.Height / _rowHeight));
        var caretRow = CaretOffset / BytesPerRow;
        var firstVisibleRow = ScrollOffset / BytesPerRow;
        if (caretRow < firstVisibleRow || caretRow >= firstVisibleRow + visibleRows)
        {
            ScrollOffset = caretRow * BytesPerRow;
            Dispatcher.UIThread.Post(InvalidateVisual);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Buffer is null)
        {
            return;
        }
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var step = shift ? 1 : 1;
        long? targetOffset = null;
        switch (e.Key)
        {
            case Key.Left:
                targetOffset = Math.Max(0, CaretOffset - step);
                break;
            case Key.Right:
                targetOffset = Math.Min(Buffer.Length - 1, CaretOffset + step);
                break;
            case Key.Up:
                targetOffset = Math.Max(0, CaretOffset - BytesPerRow);
                break;
            case Key.Down:
                targetOffset = Math.Min(Buffer.Length - 1, CaretOffset + BytesPerRow);
                break;
            case Key.PageDown:
                ScrollOffset = ClampScrollOffset(ScrollOffset + BytesPerRow * 32);
                e.Handled = true;
                return;
            case Key.PageUp:
                ScrollOffset = ClampScrollOffset(ScrollOffset - BytesPerRow * 32);
                e.Handled = true;
                return;
            case Key.Home when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                ScrollOffset = 0;
                CaretOffset = 0;
                ClearSelection();
                e.Handled = true;
                return;
            case Key.End when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                CaretOffset = Math.Max(0, Buffer.Length - 1);
                ClearSelection();
                EnsureCaretVisible();
                e.Handled = true;
                return;
            case Key.Escape:
                ClearSelection();
                e.Handled = true;
                return;
        }

        if (targetOffset is { } target)
        {
            if (shift)
            {
                if (SelectionStart < 0)
                {
                    SelectionStart = CaretOffset;
                    SelectionEnd = CaretOffset;
                }
                ExtendSelection(target);
            }
            else
            {
                CaretOffset = target;
                ClearSelection();
            }
            EnsureCaretVisible();
            e.Handled = true;
        }
    }

    private void ClearSelection()
    {
        SelectionStart = -1;
        SelectionEnd = -1;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Buffer is null)
        {
            return;
        }
        var rows = (int)Math.Round(e.Delta.Y * 3);
        ScrollOffset = ClampScrollOffset(ScrollOffset - rows * BytesPerRow);
        e.Handled = true;
    }

    private bool _dragging;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (Buffer is null)
        {
            return;
        }
        var hit = HitTestByteOffset(e.GetPosition(this));
        if (hit < 0)
        {
            return;
        }
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (shift && SelectionStart >= 0)
        {
            ExtendSelection(hit);
        }
        else
        {
            CaretOffset = hit;
            SelectionStart = hit;
            SelectionEnd = hit;
        }
        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || Buffer is null)
        {
            return;
        }
        var hit = HitTestByteOffset(e.GetPosition(this));
        if (hit < 0)
        {
            return;
        }
        ExtendSelection(hit);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private long HitTestByteOffset(Point p)
    {
        if (Buffer is null || _rowHeight <= 0 || _glyphAdvance <= 0 || BytesPerRow <= 0)
        {
            return -1;
        }
        var row = (long)(p.Y / _rowHeight);
        if (row < 0) row = 0;
        var rowStart = ScrollOffset + row * BytesPerRow;
        if (rowStart >= Buffer.Length)
        {
            return Buffer.Length - 1;
        }

        // Hex column starts after offset gutter + padding.
        var hexStart = 6.0 + OffsetColumnChars * _glyphAdvance + 12;
        var hexLocal = (p.X - hexStart) / (3 * _glyphAdvance);
        var byteInRow = (int)Math.Floor(hexLocal);
        if (byteInRow < 0)
        {
            return rowStart;
        }
        if (byteInRow >= BytesPerRow)
        {
            byteInRow = BytesPerRow - 1;
        }
        var offset = rowStart + byteInRow;
        return Math.Min(offset, Buffer.Length - 1);
    }

    private void ExtendSelection(long pivotEnd)
    {
        CaretOffset = pivotEnd;
        if (SelectionStart < 0)
        {
            SelectionStart = pivotEnd;
            SelectionEnd = pivotEnd;
            return;
        }
        if (pivotEnd >= SelectionStart)
        {
            SelectionEnd = pivotEnd;
        }
        else
        {
            // Drag/extend backwards — flip the anchor so SelectionStart <= SelectionEnd holds.
            SelectionEnd = SelectionStart;
            SelectionStart = pivotEnd;
        }
    }

    private long ClampScrollOffset(long value)
    {
        if (Buffer is null || Buffer.Length == 0)
        {
            return 0;
        }
        var maxStart = Math.Max(0, Buffer.Length - BytesPerRow);
        return Math.Clamp(value, 0, maxStart);
    }

    // ============================ ILogicalScrollable ============================
    //
    // The parent ScrollViewer reads Extent + Viewport to size its scroll thumb and reads/writes
    // Offset to drive scrolling. ScrollOffset (a byte offset in the buffer) and Offset.Y (a
    // pixel offset in scroll space) represent the same position via the row-height conversion.

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public bool IsLogicalScrollEnabled => true;

    public Size Extent
    {
        get
        {
            if (Buffer is null || _rowHeight <= 0)
            {
                return new Size(_glyphAdvance * 32, 0);
            }
            var contentWidth = OffsetColumnChars * _glyphAdvance + 12 +
                               BytesPerRow * 3 * _glyphAdvance + 12 +
                               BytesPerRow * _glyphAdvance + 12 +
                               BytesPerRow * _glyphAdvance + 12;
            return new Size(contentWidth, RowCount * _rowHeight);
        }
    }

    public Vector Offset
    {
        get
        {
            if (BytesPerRow <= 0 || _rowHeight <= 0)
            {
                return default;
            }
            var row = ScrollOffset / BytesPerRow;
            return new Vector(0, row * _rowHeight);
        }
        set
        {
            if (BytesPerRow <= 0 || _rowHeight <= 0)
            {
                return;
            }
            // Floor (not round) so thumb-drag advances row-by-row in the direction of motion
            // — rounding causes a "snap back" near half-row thresholds that reads as jitter.
            var row = (long)Math.Floor(value.Y / _rowHeight);
            row = Math.Max(0, row);
            var maxRow = Math.Max(0, RowCount - 1);
            row = Math.Min(row, maxRow);
            var newOffset = row * BytesPerRow;
            if (newOffset != ScrollOffset)
            {
                ScrollOffset = newOffset;
            }
        }
    }

    public Size Viewport => _viewport;

    public Size ScrollSize
    {
        get
        {
            MeasureGlyph();
            return new Size(_glyphAdvance, _rowHeight);
        }
    }

    public Size PageScrollSize
    {
        get
        {
            MeasureGlyph();
            return new Size(_glyphAdvance * 16, _rowHeight * 32);
        }
    }

    public event EventHandler? ScrollInvalidated;

    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);

    public bool BringIntoView(Control target, Rect targetRect) => false;

    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;
}
