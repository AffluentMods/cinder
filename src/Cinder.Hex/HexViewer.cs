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
/// Layout per row: <c>OFFSET │ HEX bytes (8-byte groups) │ text pane (ASCII or UTF-16LE)</c>, under a fixed column header.
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

    /// <summary>Search hits to paint (any IEnumerable of <see cref="HexSearchHit"/>; observable collections repaint on change).</summary>
    public static readonly StyledProperty<System.Collections.IEnumerable?> SearchHitsProperty =
        AvaloniaProperty.Register<HexViewer, System.Collections.IEnumerable?>(nameof(SearchHits));

    public System.Collections.IEnumerable? SearchHits
    {
        get => GetValue(SearchHitsProperty);
        set => SetValue(SearchHitsProperty, value);
    }

    /// <summary>Decode the text pane as UTF-16LE instead of ASCII.</summary>
    public static readonly StyledProperty<bool> TextIsUtf16Property =
        AvaloniaProperty.Register<HexViewer, bool>(nameof(TextIsUtf16));

    public bool TextIsUtf16
    {
        get => GetValue(TextIsUtf16Property);
        set => SetValue(TextIsUtf16Property, value);
    }

    /// <summary>Visible row count, for a minimap's viewport indicator.</summary>
    public static readonly DirectProperty<HexViewer, int> VisibleRowCountProperty =
        AvaloniaProperty.RegisterDirect<HexViewer, int>(nameof(VisibleRowCount), o => o.VisibleRowCount);

    private int _visibleRowCount;

    public int VisibleRowCount
    {
        get => _visibleRowCount;
        private set => SetAndRaise(VisibleRowCountProperty, ref _visibleRowCount, value);
    }

    private readonly Typeface _typeface = new("JetBrains Mono, Cascadia Mono, Consolas, monospace");
    private double _glyphAdvance;
    private double _rowHeight;
    private Size _viewport;
    private long _hoverOffset = -1;

    // Brushes cached once, reused every frame. Render is on the hot path of every wheel tick
    // and scrollbar drag; allocating SolidColorBrush per-paint produces GC churn that's
    // perceptible as scroll jank on multi-MB files. Colours track Cinder.Tokens.axaml.
    private static readonly IBrush BrushFg = new SolidColorBrush(Color.FromRgb(0xE7, 0xE9, 0xEE));        // printable
    private static readonly IBrush BrushMuted = new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xAE));
    private static readonly IBrush BrushOffset = new SolidColorBrush(Color.FromRgb(0x5F, 0x66, 0x72));    // offset gutter
    private static readonly IBrush BrushWs = new SolidColorBrush(Color.FromRgb(0x5E, 0x7C, 0x9E));        // whitespace
    private static readonly IBrush BrushCtl = new SolidColorBrush(Color.FromRgb(0xC2, 0x7C, 0x8F));       // control
    private static readonly IBrush BrushHigh = new SolidColorBrush(Color.FromRgb(0x9B, 0x8E, 0xE0));      // high / >0x7F
    private static readonly IBrush BrushNull = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x49));      // null
    private static readonly IBrush BrushAccent = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x1B));
    private static readonly IBrush BrushOnAccent = new SolidColorBrush(Color.FromRgb(0x1C, 0x0E, 0x03));  // text on caret cell
    private static readonly IBrush BrushHitBg = new SolidColorBrush(Color.FromArgb(0x40, 0xE3, 0xAE, 0x4A));
    private static readonly IBrush BrushHover = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush BrushCaretRow = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush BrushSelection = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x7A, 0x1B));
    private static readonly IBrush BrushBookmark = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA2));
    private static readonly IBrush BrushSector = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x2A));

    static HexViewer()
    {
        // Brushes constructed off the UI thread above need to be frozen for cross-thread reuse.
        ((SolidColorBrush)BrushFg).ToImmutable();
        ((SolidColorBrush)BrushMuted).ToImmutable();
        ((SolidColorBrush)BrushOffset).ToImmutable();
        ((SolidColorBrush)BrushWs).ToImmutable();
        ((SolidColorBrush)BrushCtl).ToImmutable();
        ((SolidColorBrush)BrushHigh).ToImmutable();
        ((SolidColorBrush)BrushNull).ToImmutable();
        ((SolidColorBrush)BrushAccent).ToImmutable();
        ((SolidColorBrush)BrushOnAccent).ToImmutable();
        ((SolidColorBrush)BrushHitBg).ToImmutable();
        ((SolidColorBrush)BrushHover).ToImmutable();
        ((SolidColorBrush)BrushCaretRow).ToImmutable();
        ((SolidColorBrush)BrushSelection).ToImmutable();
        ((SolidColorBrush)BrushBookmark).ToImmutable();
        ((SolidColorBrush)BrushSector).ToImmutable();

        // Tell the wrapping ScrollViewer to re-read scroll info when EXTENT changes (buffer
        // swap, BPR change). NOT when ScrollOffset changes — that creates a feedback loop where
        // the ScrollViewer's own thumb-drag setter triggers an invalidation that makes the
        // ScrollViewer re-query offset, which is the cause of the perceptible drag jank.
        BufferProperty.Changed.AddClassHandler<HexViewer>((c, _) => c.RaiseScrollInvalidated(EventArgs.Empty));
        BytesPerRowProperty.Changed.AddClassHandler<HexViewer>((c, _) => c.RaiseScrollInvalidated(EventArgs.Empty));

        // Collections repaint the grid when they change (a new bookmark or a finished search
        // must show without waiting for the next scroll).
        BookmarksProperty.Changed.AddClassHandler<HexViewer>((c, e) => c.WatchCollection(e.OldValue, e.NewValue));
        SearchHitsProperty.Changed.AddClassHandler<HexViewer>((c, e) => c.WatchCollection(e.OldValue, e.NewValue));
    }

    private void WatchCollection(object? oldValue, object? newValue)
    {
        if (oldValue is System.Collections.Specialized.INotifyCollectionChanged o)
        {
            o.CollectionChanged -= OnWatchedCollectionChanged;
        }
        if (newValue is System.Collections.Specialized.INotifyCollectionChanged n)
        {
            n.CollectionChanged += OnWatchedCollectionChanged;
        }
        InvalidateVisual();
    }

    private void OnWatchedCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(InvalidateVisual);

    public HexViewer()
    {
        Focusable = true;
        ClipToBounds = true;
        AffectsRender<HexViewer>(BufferProperty, ScrollOffsetProperty, BytesPerRowProperty,
            CaretOffsetProperty, CinderFontSizeProperty,
            SelectionStartProperty, SelectionEndProperty, TextIsUtf16Property);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureGlyph();
        var contentWidth = ContentWidth;
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
        // Layout pass, not Render: the minimap binds to this, and a property change during
        // render is rejected by the compositor ("Visual was invalidated during the render pass").
        MeasureGlyph();
        if (_rowHeight > 0)
        {
            VisibleRowCount = Math.Max(1, (int)((arranged.Height - HeaderHeight) / _rowHeight));
        }
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

        DrawHeader(context, bounds);

        var visibleRows = Math.Max(1, (int)Math.Ceiling((bounds.Height - HeaderHeight) / _rowHeight));
        var bytesPerRow = Math.Max(1, BytesPerRow);
        var sectorRules = buffer.Length >= 64 * 1024 && bytesPerRow <= 512 && 512 % bytesPerRow == 0;
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

                var y = HeaderHeight + row * _rowHeight;
                var absoluteRow = firstVisibleRow + row;

                // Sector boundaries (every 512 bytes) on disk-sized files: the unit partition
                // tables, MFT records and carvers all think in.
                if (sectorRules && row > 0 && rowOffset % 512 == 0)
                {
                    context.FillRectangle(BrushSector, new Rect(0, y, bounds.Width, 1));
                }

                // Caret row gets a faint wash across its full width; no zebra striping.
                if (absoluteRow == caretRow)
                {
                    context.FillRectangle(BrushCaretRow, new Rect(0, y, bounds.Width, _rowHeight));
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
        var offsetText = rowOffset.ToString("X" + OffsetColumnChars, CultureInfo.InvariantCulture);
        DrawText(context, offsetText, new Point(x, y), BrushOffset);
        x += OffsetColumnChars * _glyphAdvance + 12;

        // Highlight any search hits / selection intersecting this row
        DrawRowHighlights(context, rowOffset, bytesPerRow, x, y);

        var hexX = x;
        var asciiX = TextColumnX;

        for (int i = 0; i < bytesPerRow; i++)
        {
            if (i >= row.Length)
            {
                break;
            }
            var b = row[i];
            var byteOffset = rowOffset + i;
            var isCaret = byteOffset == CaretOffset;
            var isHover = byteOffset == _hoverOffset && !isCaret;
            var hxPos = HexByteX(hexX, i);
            var acPos = asciiX + i * _glyphAdvance;

            // Cell backgrounds: caret is an accent chip; hover is a faint wash across both panes.
            if (isCaret)
            {
                context.FillRectangle(BrushAccent, new Rect(hxPos - 2, y, 2 * _glyphAdvance + 4, _rowHeight), 3);
                context.FillRectangle(BrushAccent, new Rect(acPos - 1, y, _glyphAdvance + 2, _rowHeight), 3);
            }
            else if (isHover)
            {
                context.FillRectangle(BrushHover, new Rect(hxPos - 2, y, 2 * _glyphAdvance + 4, _rowHeight), 3);
                context.FillRectangle(BrushHover, new Rect(acPos - 1, y, _glyphAdvance + 2, _rowHeight), 3);
            }

            var textBrush = isCaret ? BrushOnAccent : (IsBookmarked(byteOffset) ? BrushAccent : ClassBrush(b));

            // Hex digits
            DrawText(context, b.ToString("X2", CultureInfo.InvariantCulture), new Point(hxPos, y), textBrush);

            if (TextIsUtf16)
            {
                // One glyph per little-endian code unit, drawn in the pair's first cell.
                if ((i & 1) == 0 && i + 1 < row.Length)
                {
                    var ch = (char)(row[i] | (row[i + 1] << 8));
                    var useful = IsUsefulUtf16Char(ch);
                    DrawText(context, useful ? ch.ToString() : "·", new Point(acPos, y),
                        isCaret ? BrushOnAccent : useful ? BrushFg : BrushNull);
                }
            }
            else
            {
                // ASCII glyph, coloured by the same class as its hex digits
                var c = b is >= 0x21 and < 0x7F ? (char)b : b == 0x20 ? ' ' : '·';
                DrawText(context, c.ToString(), new Point(acPos, y), textBrush);
            }
        }
    }

    private double HexColumnX => 6.0 + OffsetColumnChars * _glyphAdvance + 12;
    private double TextColumnX => HexColumnX + BytesPerRow * 3 * _glyphAdvance + HexGroupGapsTotal + 12;
    private double ContentWidth => TextColumnX + BytesPerRow * _glyphAdvance + 16;
    private double HeaderHeight => _rowHeight + 8;

    /// <summary>Fixed column header: byte offsets within the row, and the text pane's encoding.</summary>
    private void DrawHeader(DrawingContext context, Rect bounds)
    {
        context.FillRectangle(RowBrush ?? Brushes.Transparent, new Rect(0, 0, bounds.Width, HeaderHeight));
        context.FillRectangle(BrushSector, new Rect(0, HeaderHeight - 1, bounds.Width, 1));
        var y = (HeaderHeight - _rowHeight) / 2;
        DrawText(context, "Offset", new Point(6, y), BrushOffset);
        var bpr = Math.Max(1, BytesPerRow);
        for (int i = 0; i < bpr; i++)
        {
            DrawText(context, i.ToString("X2", CultureInfo.InvariantCulture), new Point(HexByteX(HexColumnX, i), y), BrushOffset);
        }
        DrawText(context, TextIsUtf16 ? "UTF-16LE" : "ASCII", new Point(TextColumnX, y), BrushOffset);
        // Thin rule between the hex and text panes.
        context.FillRectangle(BrushSector, new Rect(TextColumnX - 7, HeaderHeight, 1, Math.Max(0, bounds.Height - HeaderHeight)));
    }

    private void DrawRowHighlights(DrawingContext context, long rowOffset, int bytesPerRow,
        double hexStartX, double y)
    {
        var rowEnd = rowOffset + bytesPerRow;

        // Search hits (amber), in both panes.
        if (SearchHits is { } hits)
        {
            foreach (var item in hits)
            {
                if (item is not HexSearchHit hit)
                {
                    continue;
                }
                var hitEnd = hit.Offset + Math.Max(1, hit.Length);
                if (hit.Offset >= rowEnd || hitEnd <= rowOffset)
                {
                    continue;
                }
                DrawByteBand(context, BrushHitBg, hit.Offset, hitEnd - hit.Offset, rowOffset, bytesPerRow, hexStartX, y);
                DrawTextBand(context, BrushHitBg, hit.Offset, hitEnd - hit.Offset, rowOffset, bytesPerRow, y);
            }
        }

        // Selection (deeper orange, primary)
        if (SelectionStart >= 0 && SelectionEnd >= SelectionStart)
        {
            var selEnd = SelectionEnd + 1;
            if (SelectionStart < rowEnd && selEnd > rowOffset)
            {
                DrawByteBand(context, BrushSelection, SelectionStart, selEnd - SelectionStart, rowOffset, bytesPerRow, hexStartX, y);
                DrawTextBand(context, BrushSelection, SelectionStart, selEnd - SelectionStart, rowOffset, bytesPerRow, y);
            }
        }

        // Bookmarks (warm yellow band on the offset gutter)
        if (Bookmarks is not null)
        {
            foreach (var item in Bookmarks)
            {
                if (item is Bookmark bm && bm.Offset >= rowOffset && bm.Offset < rowEnd)
                {
                    context.FillRectangle(BrushBookmark, new Rect(0, y, 2, _rowHeight));
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
        var left = HexByteX(hexStartX, localStart);
        var right = HexByteX(hexStartX, localEnd - 1) + 2 * _glyphAdvance;
        context.FillRectangle(brush, new Rect(left - 1, y, right - left + 2, _rowHeight), 3);
    }

    private void DrawTextBand(DrawingContext context, IBrush brush, long start, long length,
        long rowOffset, int bytesPerRow, double y)
    {
        var localStart = (int)Math.Max(0, start - rowOffset);
        var localEnd = (int)Math.Min(bytesPerRow, start + length - rowOffset);
        if (localEnd <= localStart)
        {
            return;
        }
        var left = TextColumnX + localStart * _glyphAdvance;
        context.FillRectangle(brush, new Rect(left - 1, y, (localEnd - localStart) * _glyphAdvance + 2, _rowHeight), 2);
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
        // 20 px rows at the default size: dense, but with enough leading that a 16-byte row
        // reads as one line and the caret chip doesn't touch its neighbours.
        _rowHeight = Math.Ceiling(ft.Height * 1.3);
    }

    /// <summary>Offset gutter width in hex digits: 8 for images up to 4 GiB, 16 beyond.</summary>
    private int OffsetColumnChars => Buffer is { Length: > 0xFFFFFFFFL } ? 16 : 8;

    // Extra spacing after every 8-byte group in the hex column (the 8-byte split).
    private double GroupGap => _glyphAdvance * 1.6;
    private double HexGroupGapsTotal => Math.Max(0, (BytesPerRow - 1) / 8) * GroupGap;

    /// <summary>X of hex byte i within the hex column, including 8-byte group gaps.</summary>
    private double HexByteX(double hexBaseX, int i) => hexBaseX + i * 3 * _glyphAdvance + (i / 8) * GroupGap;

    private static IBrush ClassBrush(byte b) => b switch
    {
        0 => BrushNull,
        9 or 10 or 13 or 32 => BrushWs,
        < 32 or 127 => BrushCtl,
        > 127 => BrushHigh,
        _ => BrushFg,
    };

    /// <summary>Compute total row count for the current buffer + bytes-per-row.</summary>
    public long RowCount => Buffer is null ? 0 : (Buffer.Length + BytesPerRow - 1) / BytesPerRow;

    public void EnsureCaretVisible()
    {
        var buffer = Buffer;
        if (buffer is null || _rowHeight <= 0)
        {
            return;
        }
        var visibleRows = Math.Max(1, (int)((Bounds.Height - HeaderHeight) / _rowHeight));
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
        if (Buffer is null)
        {
            return;
        }
        var hit = HitTestByteOffset(e.GetPosition(this));

        // Track the hovered byte so the render can wash it across both panes. Invalidate only
        // when it actually changes — pointer-move fires far faster than we need to repaint.
        if (!_dragging && hit != _hoverOffset)
        {
            _hoverOffset = hit;
            InvalidateVisual();
        }

        if (!_dragging || hit < 0)
        {
            return;
        }
        ExtendSelection(hit);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverOffset != -1)
        {
            _hoverOffset = -1;
            InvalidateVisual();
        }
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
        var row = (long)((p.Y - HeaderHeight) / _rowHeight);
        if (row < 0) row = 0;
        var rowStart = ScrollOffset + row * BytesPerRow;
        if (rowStart >= Buffer.Length)
        {
            return Buffer.Length - 1;
        }

        // Column origins mirror DrawRow.
        var hexStart = HexColumnX;
        var asciiStart = TextColumnX - 6;

        int byteInRow;
        if (p.X >= asciiStart)
        {
            // Clicks in the ASCII pane select the same byte.
            byteInRow = (int)Math.Floor((p.X - asciiStart) / _glyphAdvance);
        }
        else if (p.X < hexStart)
        {
            return rowStart;
        }
        else
        {
            // Walk the hex cells so the 8-byte group gap maps correctly.
            byteInRow = 0;
            for (int i = 0; i < BytesPerRow; i++)
            {
                if (p.X < HexByteX(hexStart, i) + 3 * _glyphAdvance)
                {
                    byteInRow = i;
                    break;
                }
                byteInRow = i;
            }
        }

        if (byteInRow < 0)
        {
            byteInRow = 0;
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
            return new Size(ContentWidth, RowCount * _rowHeight + HeaderHeight);
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
