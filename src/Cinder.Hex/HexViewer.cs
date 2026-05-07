using System.Globalization;
using Avalonia;
using Avalonia.Controls;
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
/// </summary>
public sealed class HexViewer : Control
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
    public IList<Bookmark> Bookmarks { get; } = new List<Bookmark>();
    public IList<StructureOverlay> Overlays { get; } = new List<StructureOverlay>();

    private Typeface _typeface = new("Cascadia Mono, Consolas, monospace");
    private double _glyphAdvance;
    private double _rowHeight;

    public HexViewer()
    {
        Focusable = true;
        ClipToBounds = true;
        AffectsRender<HexViewer>(BufferProperty, ScrollOffsetProperty, BytesPerRowProperty,
            CaretOffsetProperty, CinderFontSizeProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureGlyph();
        var width = OffsetColumnChars * _glyphAdvance + 12 +
                    BytesPerRow * 3 * _glyphAdvance + 12 +
                    BytesPerRow * _glyphAdvance + 12 +
                    BytesPerRow * _glyphAdvance + 12;
        return new Size(Math.Max(width, availableSize.Width), availableSize.Height);
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
            DrawCenteredText(context, "(no buffer)", bounds);
            return;
        }

        var visibleRows = Math.Max(1, (int)Math.Ceiling(bounds.Height / _rowHeight));
        var bytesPerRow = Math.Max(1, BytesPerRow);
        var capacity = visibleRows * bytesPerRow;
        var read = new byte[capacity];
        var bytesRead = buffer.Read(ScrollOffset, read);

        var fg = Brushes.Gainsboro;
        var muted = new SolidColorBrush(Color.FromRgb(0x9C, 0xA0, 0xAB));
        var accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x1A));
        var hitBg = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0x7A, 0x1A));
        var caretBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47));

        for (int row = 0; row < visibleRows; row++)
        {
            var rowOffset = ScrollOffset + (long)row * bytesPerRow;
            if (rowOffset >= buffer.Length)
            {
                break;
            }

            var y = row * _rowHeight;
            DrawRow(context, rowOffset, read.AsSpan(row * bytesPerRow, Math.Min(bytesPerRow, Math.Max(0, bytesRead - row * bytesPerRow))),
                bytesPerRow, y, fg, muted, accent, hitBg, caretBrush);
        }
    }

    private void DrawRow(DrawingContext context, long rowOffset, ReadOnlySpan<byte> row, int bytesPerRow,
        double y, IBrush fg, IBrush muted, IBrush accent, IBrush hitBg, IBrush caretBrush)
    {
        var x = 6.0;

        // Offset column
        var offsetText = rowOffset.ToString("X16", CultureInfo.InvariantCulture);
        DrawText(context, offsetText, new Point(x, y), muted);
        x += OffsetColumnChars * _glyphAdvance + 12;

        // Highlight any search hits intersecting this row
        DrawRowHighlights(context, rowOffset, bytesPerRow, x, y, hitBg);

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
            var brush = byteOffset == CaretOffset ? caretBrush : (IsBookmarked(byteOffset) ? accent : fg);
            DrawText(context, byteText, new Point(hexX + i * 3 * _glyphAdvance, y), brush);
        }
        x += bytesPerRow * 3 * _glyphAdvance + 12;

        // ASCII column
        var asciiX = x;
        for (int i = 0; i < row.Length; i++)
        {
            var b = row[i];
            var c = b is >= 0x20 and < 0x7F ? (char)b : '.';
            DrawText(context, c.ToString(), new Point(asciiX + i * _glyphAdvance, y), muted);
        }
        x += bytesPerRow * _glyphAdvance + 12;

        // UTF-16LE column (one glyph per 2 bytes)
        var utfX = x;
        for (int i = 0; i + 1 < row.Length; i += 2)
        {
            var ch = (char)(row[i] | (row[i + 1] << 8));
            var glyph = char.IsControl(ch) || ch > 0xFFFD ? '.' : ch;
            DrawText(context, glyph.ToString(), new Point(utfX + i * _glyphAdvance / 2, y), muted);
        }
    }

    private void DrawRowHighlights(DrawingContext context, long rowOffset, int bytesPerRow,
        double hexStartX, double y, IBrush hitBg)
    {
        var rowEnd = rowOffset + bytesPerRow;
        foreach (var hit in Highlights)
        {
            var hitEnd = hit.Offset + hit.Length;
            if (hit.Offset >= rowEnd || hitEnd <= rowOffset)
            {
                continue;
            }
            var localStart = (int)Math.Max(0, hit.Offset - rowOffset);
            var localEnd = (int)Math.Min(bytesPerRow, hitEnd - rowOffset);
            var rect = new Rect(
                hexStartX + localStart * 3 * _glyphAdvance,
                y,
                (localEnd - localStart) * 3 * _glyphAdvance,
                _rowHeight);
            context.FillRectangle(hitBg, rect);
        }
    }

    private bool IsBookmarked(long offset)
    {
        for (int i = 0; i < Bookmarks.Count; i++)
        {
            if (Bookmarks[i].Offset == offset)
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
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 16 : 1;
        switch (e.Key)
        {
            case Key.Left:
                CaretOffset = Math.Max(0, CaretOffset - step);
                EnsureCaretVisible();
                e.Handled = true;
                break;
            case Key.Right:
                CaretOffset = Math.Min(Buffer.Length - 1, CaretOffset + step);
                EnsureCaretVisible();
                e.Handled = true;
                break;
            case Key.Up:
                CaretOffset = Math.Max(0, CaretOffset - BytesPerRow);
                EnsureCaretVisible();
                e.Handled = true;
                break;
            case Key.Down:
                CaretOffset = Math.Min(Buffer.Length - 1, CaretOffset + BytesPerRow);
                EnsureCaretVisible();
                e.Handled = true;
                break;
            case Key.PageDown:
                ScrollOffset = Math.Min(Buffer.Length - 1, ScrollOffset + BytesPerRow * 32);
                e.Handled = true;
                break;
            case Key.PageUp:
                ScrollOffset = Math.Max(0, ScrollOffset - BytesPerRow * 32);
                e.Handled = true;
                break;
            case Key.Home when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                ScrollOffset = 0;
                CaretOffset = 0;
                e.Handled = true;
                break;
            case Key.End when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                CaretOffset = Math.Max(0, Buffer.Length - 1);
                EnsureCaretVisible();
                e.Handled = true;
                break;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Buffer is null)
        {
            return;
        }
        var rows = (int)Math.Round(e.Delta.Y * 3);
        ScrollOffset = Math.Clamp(ScrollOffset - rows * BytesPerRow, 0, Math.Max(0, Buffer.Length - BytesPerRow));
        e.Handled = true;
    }
}
