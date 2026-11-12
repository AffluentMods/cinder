namespace Cinder.Hex;

/// <summary>
/// The offset arithmetic behind <see cref="HexViewer"/>: scroll clamping, caret movement,
/// selection extension and text-pane hit testing. Kept free of Avalonia so it can be tested
/// without a window — every function here once had a boundary bug that only showed at
/// end-of-file, on an empty file, or when selecting backwards.
/// </summary>
internal static class HexViewerMath
{
    /// <summary>
    /// The largest valid scroll offset: the start of the row holding the last byte. Always a
    /// multiple of <paramref name="bytesPerRow"/>, so rows stay on the grid at end-of-file.
    /// </summary>
    public static long MaxScrollOffset(long length, int bytesPerRow)
        => length <= 0 || bytesPerRow <= 0 ? 0 : (length - 1) / bytesPerRow * bytesPerRow;

    /// <summary>
    /// Clamps a requested scroll offset into the buffer and snaps it down to a row boundary.
    ///
    /// <para>The upper bound used to be <c>length - bytesPerRow</c>, which is only a row start
    /// when the length is a multiple of the row size. Scrolling to the end of any other file
    /// left every row shifted by <c>length % bytesPerRow</c>, and the shift survived scrolling
    /// back up because each step moved by whole rows from the skewed position.</para>
    /// </summary>
    public static long ClampScrollOffset(long value, long length, int bytesPerRow)
    {
        if (length <= 0 || bytesPerRow <= 0 || value <= 0)
        {
            return 0;
        }
        var max = MaxScrollOffset(length, bytesPerRow);
        return value >= max ? max : value - value % bytesPerRow;
    }

    /// <summary>
    /// Moves the caret by <paramref name="delta"/> bytes, staying inside the buffer. For an
    /// empty buffer the only caret position is 0 — never <c>length - 1</c>, which is -1.
    /// </summary>
    public static long MoveCaret(long caret, long delta, long length)
    {
        if (length <= 0)
        {
            return 0;
        }
        long target;
        if (delta >= 0)
        {
            target = caret > long.MaxValue - delta ? long.MaxValue : caret + delta;
        }
        else
        {
            target = caret < long.MinValue - delta ? long.MinValue : caret + delta;
        }
        return Math.Clamp(target, 0, length - 1);
    }

    /// <summary>
    /// The selection that runs between a fixed <paramref name="anchor"/> and the byte the caret
    /// just moved to, as an inclusive <c>(Start, End)</c> with <c>Start &lt;= End</c>.
    /// </summary>
    public static (long Start, long End) SelectionBetween(long anchor, long pivot)
        => anchor <= pivot ? (anchor, pivot) : (pivot, anchor);

    /// <summary>
    /// The anchor to extend a selection from.
    ///
    /// <para>The control remembers where a selection began (<paramref name="storedAnchor"/>),
    /// but the selection can also be set from outside it — a search hit, a bookmark jump. The
    /// stored value is used only while it is still one end of the current selection; otherwise
    /// the end the caret is not on becomes the anchor. With no selection at all, the caret is
    /// the anchor.</para>
    /// </summary>
    public static long ResolveAnchor(long storedAnchor, long selectionStart, long selectionEnd, long caret)
    {
        if (selectionStart < 0 || selectionEnd < selectionStart)
        {
            return Math.Max(0, caret);
        }
        if (storedAnchor == selectionStart || storedAnchor == selectionEnd)
        {
            return storedAnchor;
        }
        return caret == selectionStart ? selectionEnd : selectionStart;
    }

    /// <summary>
    /// The byte index within a row for a pointer at <paramref name="x"/> in the text pane.
    /// Cell <c>i</c> is drawn at <c>textColumnX + i * glyphAdvance</c>, so that is the origin
    /// to measure from. (It used to be measured from 6 px to the left of that, the edge where
    /// the pane starts taking clicks, which mapped most of every cell to the next byte.)
    /// </summary>
    public static int TextPaneByteIndex(double x, double textColumnX, double glyphAdvance, int bytesPerRow)
    {
        if (bytesPerRow <= 0 || !(glyphAdvance > 0))
        {
            return 0;
        }
        var cell = Math.Floor((x - textColumnX) / glyphAdvance);
        if (!(cell > 0))
        {
            return 0;   // left of the first glyph, or NaN
        }
        return cell >= bytesPerRow - 1 ? bytesPerRow - 1 : (int)cell;
    }

    /// <summary>True when the row starting at <paramref name="rowOffset"/> holds the caret.</summary>
    public static bool RowContains(long rowOffset, int bytesPerRow, long offset)
        => offset >= rowOffset && offset - rowOffset < bytesPerRow;
}
