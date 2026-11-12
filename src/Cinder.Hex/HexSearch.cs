using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace Cinder.Hex;

public enum HexSearchKind
{
    Hex,
    Ascii,
    Utf16Le,
    Utf16Be,
    Regex,
}

/// <summary>
/// What to look for. <see cref="CaseSensitive"/> only ever applies to letters in text queries:
/// a <see cref="HexSearchKind.Hex"/> query names exact bytes and is matched exactly whatever
/// this flag says, and for the UTF-16 kinds only code units in the ASCII letter range fold.
/// </summary>
public sealed record HexSearchOptions(
    HexSearchKind Kind,
    string Query,
    bool CaseSensitive = true,
    long StartOffset = 0,
    long? EndOffset = null);

public sealed record HexSearchHit(long Offset, int Length);

/// <summary>
/// One batch of hits from <see cref="HexSearch.FindNext"/> or <see cref="HexSearch.FindPrevious"/>.
/// </summary>
/// <param name="Hits">
/// The hits found, nearest first: ascending offsets for a forward search, descending for a
/// backward one. Never more than the cap that was asked for.
/// </param>
/// <param name="ReachedEnd">
/// True when the search ran to the end of its range (the start of it, going backwards): there
/// are no matches beyond the ones in <see cref="Hits"/>. False when it stopped because the cap
/// was reached — more matches may exist, and <see cref="ContinueFrom"/> says where to look.
/// </param>
/// <param name="ContinueFrom">
/// Null when <see cref="ReachedEnd"/> is true. Otherwise the offset to pass to the next call in
/// the same direction to get the following batch without repeating or skipping a hit:
/// <c>fromOffset</c> for <see cref="HexSearch.FindNext"/> (one past the last hit's offset),
/// <c>beforeOffset</c> for <see cref="HexSearch.FindPrevious"/> (the last hit's offset).
/// </param>
public sealed record HexSearchPage(IReadOnlyList<HexSearchHit> Hits, bool ReachedEnd, long? ContinueFrom)
{
    /// <summary>True when the batch was cut off by the cap rather than by the end of the range.</summary>
    public bool StoppedAtCap => !ReachedEnd;
}

/// <summary>
/// Streaming search over an <see cref="IHexBuffer"/>. Operates in 1 MiB windows with an overlap
/// equal to <c>pattern.Length - 1</c> so matches straddling a window boundary are still found.
/// Yields hits as it finds them so the UI can render them progressively.
/// </summary>
public static class HexSearch
{
    private const int ChunkSize = 1 << 20;

    /// <summary>
    /// Upper bound on the hex query text we will accept. Guards the decode path against an
    /// unbounded allocation driven by a pasted query string.
    /// </summary>
    private const int MaxHexQueryChars = 1 << 16;

    /// <summary>
    /// Every hit in the range the options describe (<see cref="HexSearchOptions.StartOffset"/>
    /// up to <see cref="HexSearchOptions.EndOffset"/> or the end of the buffer), in ascending
    /// order. A hit lies wholly inside the range.
    /// </summary>
    public static IEnumerable<HexSearchHit> Search(IHexBuffer buffer, HexSearchOptions options, CancellationToken ct = default)
        => SearchFrom(buffer, options, long.MinValue, ct);

    /// <summary>
    /// The hits of <see cref="Search"/> whose offset is at or after <paramref name="fromOffset"/>,
    /// in ascending order — exactly that subset, whatever the kind of search. In particular a
    /// regex search keeps the window grid of the full search (anchored at the range start, not
    /// at <paramref name="fromOffset"/>), so resuming never finds or loses a match the full
    /// search would not; and a literal match straddling a window boundary is found as always.
    /// </summary>
    public static IEnumerable<HexSearchHit> SearchFrom(IHexBuffer buffer, HexSearchOptions options, long fromOffset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);

        if (!TryGetRange(buffer, options, out var start, out var end))
        {
            return [];
        }
        var from = Math.Max(start, fromOffset);
        if (from >= end)
        {
            return [];
        }

        if (options.Kind == HexSearchKind.Regex)
        {
            return SearchRegex(buffer, options, start, end, from, ct);
        }

        var pattern = BuildPattern(options);
        if (pattern.Length == 0 || pattern.Length > end - from)
        {
            return [];
        }

        // A literal match does not depend on where the scan began, so starting at `from` gives
        // the same hits as filtering the full search.
        var foldable = options.CaseSensitive ? null : BuildFoldMask(options.Kind, pattern);
        return SearchLiteral(buffer, pattern, foldable, from, end, ct);
    }

    /// <summary>
    /// The hits of <see cref="Search"/> whose offset is before <paramref name="beforeOffset"/>,
    /// in descending order (nearest first) — the same set a forward search finds, read from
    /// the other end. The buffer is scanned backwards in windows, so the cost is that of the
    /// distance actually covered, not of the whole range.
    /// </summary>
    public static IEnumerable<HexSearchHit> SearchBackward(IHexBuffer buffer, HexSearchOptions options, long beforeOffset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);

        if (!TryGetRange(buffer, options, out var start, out var end))
        {
            return [];
        }
        var before = Math.Min(end, beforeOffset);
        if (before <= start)
        {
            return [];
        }

        if (options.Kind == HexSearchKind.Regex)
        {
            return SearchRegexBackward(buffer, options, start, end, before, ct);
        }

        var pattern = BuildPattern(options);
        if (pattern.Length == 0 || pattern.Length > end - start)
        {
            return [];
        }

        var foldable = options.CaseSensitive ? null : BuildFoldMask(options.Kind, pattern);
        return SearchLiteralBackward(buffer, pattern, foldable, start, end, before, ct);
    }

    /// <summary>
    /// The next batch of hits at or after <paramref name="fromOffset"/>: at most
    /// <paramref name="maxHits"/>, ascending. See <see cref="HexSearchPage"/> for how to tell
    /// "no more matches" from "stopped at the cap" and how to continue.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxHits"/> is not positive.</exception>
    public static HexSearchPage FindNext(IHexBuffer buffer, HexSearchOptions options, long fromOffset, int maxHits, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHits);
        return TakePage(SearchFrom(buffer, options, fromOffset, ct), maxHits, static last => last.Offset == long.MaxValue ? long.MaxValue : last.Offset + 1);
    }

    /// <summary>
    /// The next batch of hits before <paramref name="beforeOffset"/>: at most
    /// <paramref name="maxHits"/>, descending (nearest first). See <see cref="HexSearchPage"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxHits"/> is not positive.</exception>
    public static HexSearchPage FindPrevious(IHexBuffer buffer, HexSearchOptions options, long beforeOffset, int maxHits, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHits);
        return TakePage(SearchBackward(buffer, options, beforeOffset, ct), maxHits, static last => last.Offset);
    }

    private static HexSearchPage TakePage(IEnumerable<HexSearchHit> hits, int maxHits, Func<HexSearchHit, long> continueAfter)
    {
        var page = new List<HexSearchHit>(Math.Min(maxHits, 1024));
        foreach (var hit in hits)
        {
            page.Add(hit);
            if (page.Count >= maxHits)
            {
                // Stopped at the cap. Whether anything lies beyond is not known without
                // scanning on — which is what the next call does.
                return new HexSearchPage(page, ReachedEnd: false, ContinueFrom: continueAfter(hit));
            }
        }
        return new HexSearchPage(page, ReachedEnd: true, ContinueFrom: null);
    }

    /// <summary>The byte range a search covers; false when it is empty.</summary>
    private static bool TryGetRange(IHexBuffer buffer, HexSearchOptions options, out long start, out long end)
    {
        end = options.EndOffset ?? buffer.Length;
        if (end > buffer.Length)
        {
            end = buffer.Length;
        }
        start = Math.Max(0, options.StartOffset);
        return start < end;
    }

    /// <summary>
    /// Which pattern bytes may match either case of an ASCII letter, or null when none can.
    ///
    /// <para>Folding is a statement about text, so it is decided per byte from what that byte
    /// means in the query's encoding. A hex query is bytes, not text: nothing folds, and
    /// <c>4D 5A</c> never matches <c>6D 7A</c>. In UTF-16 a byte is a letter only when it is the
    /// low half of a code unit whose high half is zero; folding every byte in 0x41–0x5A would
    /// also "fold" the high bytes of unrelated characters (U+4E2D and U+6E2D differ by exactly
    /// that bit) and report text that is not in the evidence.</para>
    /// </summary>
    internal static bool[]? BuildFoldMask(HexSearchKind kind, byte[] pattern)
    {
        static bool IsAsciiLetter(byte b) => (uint)((b | 0x20) - 'a') <= 'z' - 'a';

        bool[]? mask = null;
        switch (kind)
        {
            case HexSearchKind.Ascii:
                for (int i = 0; i < pattern.Length; i++)
                {
                    if (IsAsciiLetter(pattern[i]))
                    {
                        (mask ??= new bool[pattern.Length])[i] = true;
                    }
                }
                break;

            case HexSearchKind.Utf16Le:
            case HexSearchKind.Utf16Be:
                // The pattern is whole code units, so unit boundaries are known from the
                // pattern itself wherever in the buffer it happens to match.
                var low = kind == HexSearchKind.Utf16Le ? 0 : 1;
                var high = 1 - low;
                for (int unit = 0; unit + 1 < pattern.Length; unit += 2)
                {
                    if (pattern[unit + high] == 0 && IsAsciiLetter(pattern[unit + low]))
                    {
                        (mask ??= new bool[pattern.Length])[unit + low] = true;
                    }
                }
                break;
        }
        return mask;
    }

    private static IEnumerable<HexSearchHit> SearchLiteral(
        IHexBuffer buffer,
        byte[] pattern,
        bool[]? foldable,
        long start,
        long end,
        CancellationToken ct)
    {
        // The overlap keeps the last (pattern.Length - 1) bytes of each window in play so a
        // match spanning a window boundary is still found. Windows advance by
        // (read - overlap), so the retained tail is re-scanned exactly once as the head of
        // the next window; hits inside it are reported from whichever window sees them first.
        var overlap = pattern.Length - 1;
        var window = new byte[ChunkSize + overlap];
        var pos = start;
        long lastReported = -1;

        while (pos < end)
        {
            ct.ThrowIfCancellationRequested();

            var want = (int)Math.Min(window.Length, end - pos);
            var read = ReadFull(buffer, pos, window.AsSpan(0, want));
            if (read <= 0)
            {
                yield break;
            }

            var local = 0;
            while (true)
            {
                var idx = IndexOfArray(window, local, read - local, pattern, foldable);
                if (idx < 0)
                {
                    break;
                }

                // Suppress the duplicate a boundary-spanning hit would otherwise produce when
                // it falls inside the retained overlap of the following window.
                var absolute = pos + idx;
                if (absolute > lastReported)
                {
                    lastReported = absolute;
                    yield return new HexSearchHit(absolute, pattern.Length);
                }
                local = idx + 1;
            }

            // Everything up to `end` has now been scanned — nothing left to advance into.
            // This is the loop's real terminator: a short read is not required, and must not
            // be relied on, because IHexBuffer implementations normally fill every request.
            if (pos + read >= end)
            {
                yield break;
            }

            // Advance past the region this window owned, retaining the overlap tail. A
            // non-positive step would spin forever, so treat it as end-of-search.
            var advance = read - overlap;
            if (advance <= 0)
            {
                yield break;
            }
            pos += advance;
        }
    }

    /// <summary>
    /// Literal search from the high end. Each window owns the hit starts in <c>[lo, hi)</c> and
    /// reads <c>pattern.Length - 1</c> bytes past <c>hi</c>, so a hit that starts in the window
    /// and ends beyond it is complete; the next window down ends where this one began, so no
    /// start is seen twice. Hits within a window are found forwards and handed out in reverse.
    /// </summary>
    private static IEnumerable<HexSearchHit> SearchLiteralBackward(
        IHexBuffer buffer,
        byte[] pattern,
        bool[]? foldable,
        long start,
        long end,
        long before,
        CancellationToken ct)
    {
        // The highest offset a hit may start at: before the caller's bound, and early enough
        // to end inside the range.
        var hi = Math.Min(before, end - pattern.Length + 1);
        if (hi <= start)
        {
            yield break;
        }

        var window = new byte[ChunkSize + pattern.Length - 1];
        var found = new List<int>();
        while (hi > start)
        {
            ct.ThrowIfCancellationRequested();

            var lo = Math.Max(start, hi - ChunkSize);
            var want = (int)(hi - lo) + pattern.Length - 1;
            var read = ReadFull(buffer, lo, window.AsSpan(0, want));

            found.Clear();
            var local = 0;
            while (true)
            {
                var idx = IndexOfArray(window, local, read - local, pattern, foldable);
                if (idx < 0)
                {
                    break;
                }
                found.Add(idx);
                local = idx + 1;
            }
            for (int i = found.Count - 1; i >= 0; i--)
            {
                yield return new HexSearchHit(lo + found[i], pattern.Length);
            }

            hi = lo;
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/> from <paramref name="buffer"/>, looping over short
    /// reads. <see cref="IHexBuffer.Read"/> is permitted to return fewer bytes than asked for;
    /// the search windows must not silently shrink because of it.
    /// </summary>
    private static int ReadFull(IHexBuffer buffer, long offset, Span<byte> destination)
    {
        var filled = 0;
        while (filled < destination.Length)
        {
            var n = buffer.Read(offset + filled, destination[filled..]);
            if (n <= 0)
            {
                break;
            }
            filled += n;
        }
        return filled;
    }

    // Encoders that refuse rather than substitute. The defaults replace anything they cannot
    // represent with '?' (ASCII) or U+FFFD (a lone surrogate in UTF-16), which silently turns
    // the search into one for a different string.
    private static readonly Encoding StrictUtf16Le = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The exact bytes to search for.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The query cannot be expressed in the chosen kind: an ASCII query containing a
    /// non-ASCII character, or a UTF-16 query containing an unpaired surrogate. The search is
    /// refused rather than run for a substituted character.
    /// </exception>
    internal static byte[] BuildPattern(HexSearchOptions options)
    {
        return options.Kind switch
        {
            HexSearchKind.Hex => DecodeHex(options.Query),
            HexSearchKind.Ascii => EncodeAscii(options.Query),
            HexSearchKind.Utf16Le => EncodeStrict(StrictUtf16Le, options.Query),
            HexSearchKind.Utf16Be => EncodeStrict(StrictUtf16Be, options.Query),
            HexSearchKind.Regex => [], // handled separately
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    private static byte[] EncodeAscii(string query)
    {
        var bytes = new byte[query.Length];
        for (int i = 0; i < query.Length; i++)
        {
            var c = query[i];
            if (c > 0x7F)
            {
                // Which bytes "é" is depends on an encoding the evidence does not declare
                // (Latin-1, Windows-1252, UTF-8, UTF-16 …). Guessing would report matches for
                // text the examiner did not type; Encoding.ASCII's answer — search for '?'
                // instead — is worse.
                throw new ArgumentException(
                    $"ASCII search text contains a non-ASCII character ('{c}', U+{(int)c:X4}) at position {i + 1}. " +
                    "Search for it as UTF-16, or enter the exact bytes as Hex.",
                    nameof(query));
            }
            bytes[i] = (byte)c;
        }
        return bytes;
    }

    private static byte[] EncodeStrict(Encoding encoding, string query)
    {
        try
        {
            return encoding.GetBytes(query);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException(
                $"UTF-16 search text contains an unpaired surrogate at position {ex.Index + 1}; it cannot be searched for as text. Enter the exact bytes as Hex.",
                nameof(query), ex);
        }
    }

    private static byte[] DecodeHex(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaxHexQueryChars)
        {
            return [];
        }

        // Rented rather than stackalloc: `query` is user input and an over-long paste would
        // otherwise blow the stack.
        var pool = ArrayPool<char>.Shared;
        var scratch = pool.Rent(query.Length);
        try
        {
            var n = 0;
            foreach (var c in query)
            {
                if (char.IsWhiteSpace(c) || c == '-' || c == ':' || c == ',')
                {
                    continue;
                }
                scratch[n++] = c;
            }

            if (n == 0 || n % 2 != 0)
            {
                return [];
            }

            var src = scratch.AsSpan(0, n);
            var result = new byte[n / 2];
            for (int i = 0; i < result.Length; i++)
            {
                if (!byte.TryParse(src.Slice(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out result[i]))
                {
                    return [];
                }
            }
            return result;
        }
        finally
        {
            pool.Return(scratch);
        }
    }

    /// <summary>
    /// Index-based array search returning the absolute index into <paramref name="haystack"/>, or -1.
    /// <paramref name="foldable"/> marks the needle positions where either case of an ASCII
    /// letter matches; null means an exact byte match.
    /// </summary>
    private static int IndexOfArray(byte[] haystack, int start, int count, byte[] needle, bool[]? foldable)
    {
        if (count < needle.Length)
        {
            return -1;
        }

        // An exact search delegates to the vectorized span scan. The folding path compares
        // byte by byte, but only at positions whose first byte already matches.
        if (foldable is null)
        {
            var idx = haystack.AsSpan(start, count).IndexOf(needle.AsSpan());
            return idx < 0 ? -1 : start + idx;
        }

        var end = start + count;
        for (int i = start; i + needle.Length <= end; i++)
        {
            var match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                var h = haystack[i + j];
                var n = needle[j];
                if (h != n && !(foldable[j] && ToLowerAscii(h) == ToLowerAscii(n)))
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return i;
            }
        }
        return -1;
    }

    private static byte ToLowerAscii(byte b) => (byte)(b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : b);

    private static Regex? TryCreateRegex(HexSearchOptions options)
    {
        var rxOpts = options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        try
        {
            return new Regex(options.Query, rxOpts | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // Regex runs over Latin-1-decoded windows (byte-for-char, so offsets stay exact).
    // Matches straddling a window boundary are not found — surfaced in the tool's help
    // text; narrow the range and re-run if a match is expected near a 1 MiB boundary.
    //
    // The windows are a fixed grid: window k covers [start + k MiB, start + (k + 1) MiB). A
    // search resumed at `from`, or run backwards, uses that same grid and filters by offset,
    // which is what makes it return exactly the hits of the full search and no others.

    private static IEnumerable<HexSearchHit> SearchRegex(
        IHexBuffer buffer,
        HexSearchOptions options,
        long start,
        long end,
        long from,
        CancellationToken ct)
    {
        if (TryCreateRegex(options) is not { } regex)
        {
            yield break;
        }

        var bytes = new byte[ChunkSize];
        var pos = start + (from - start) / ChunkSize * ChunkSize;
        while (pos < end)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(bytes.Length, end - pos);
            var read = ReadFull(buffer, pos, bytes.AsSpan(0, want));
            if (read <= 0)
            {
                yield break;
            }
            var text = Encoding.Latin1.GetString(bytes, 0, read);

            foreach (Match m in regex.Matches(text))
            {
                var offset = pos + m.Index;
                if (offset >= from)
                {
                    yield return new HexSearchHit(offset, m.Length);
                }
            }

            pos += read;
        }
    }

    private static IEnumerable<HexSearchHit> SearchRegexBackward(
        IHexBuffer buffer,
        HexSearchOptions options,
        long start,
        long end,
        long before,
        CancellationToken ct)
    {
        if (TryCreateRegex(options) is not { } regex)
        {
            yield break;
        }

        var bytes = new byte[ChunkSize];
        var inWindow = new List<HexSearchHit>();
        for (var k = (before - 1 - start) / ChunkSize; k >= 0; k--)
        {
            ct.ThrowIfCancellationRequested();
            var pos = start + k * ChunkSize;
            var want = (int)Math.Min(bytes.Length, end - pos);
            var read = ReadFull(buffer, pos, bytes.AsSpan(0, want));
            if (read <= 0)
            {
                continue;
            }
            var text = Encoding.Latin1.GetString(bytes, 0, read);

            inWindow.Clear();
            foreach (Match m in regex.Matches(text))
            {
                var offset = pos + m.Index;
                if (offset < before)
                {
                    inWindow.Add(new HexSearchHit(offset, m.Length));
                }
            }
            for (int i = inWindow.Count - 1; i >= 0; i--)
            {
                yield return inWindow[i];
            }
        }
    }
}
