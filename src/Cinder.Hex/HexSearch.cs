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

public sealed record HexSearchOptions(
    HexSearchKind Kind,
    string Query,
    bool CaseSensitive = true,
    long StartOffset = 0,
    long? EndOffset = null);

public sealed record HexSearchHit(long Offset, int Length);

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

    public static IEnumerable<HexSearchHit> Search(IHexBuffer buffer, HexSearchOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);

        var end = options.EndOffset ?? buffer.Length;
        if (end > buffer.Length)
        {
            end = buffer.Length;
        }

        var start = Math.Max(0, options.StartOffset);
        if (start >= end)
        {
            return [];
        }

        if (options.Kind == HexSearchKind.Regex)
        {
            return SearchRegex(buffer, options, start, end, ct);
        }

        var pattern = BuildPattern(options);
        if (pattern.Length == 0 || pattern.Length > end - start)
        {
            return [];
        }

        return SearchLiteral(buffer, options, pattern, start, end, ct);
    }

    private static IEnumerable<HexSearchHit> SearchLiteral(
        IHexBuffer buffer,
        HexSearchOptions options,
        byte[] pattern,
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
                var idx = IndexOfArray(window, local, read - local, pattern, options.CaseSensitive);
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

    private static byte[] BuildPattern(HexSearchOptions options)
    {
        return options.Kind switch
        {
            HexSearchKind.Hex => DecodeHex(options.Query),
            HexSearchKind.Ascii => Encoding.ASCII.GetBytes(options.Query),
            HexSearchKind.Utf16Le => Encoding.Unicode.GetBytes(options.Query),
            HexSearchKind.Utf16Be => Encoding.BigEndianUnicode.GetBytes(options.Query),
            HexSearchKind.Regex => [], // handled separately
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
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

    /// <summary>Index-based array search returning the absolute index into <paramref name="haystack"/>, or -1.</summary>
    private static int IndexOfArray(byte[] haystack, int start, int count, byte[] needle, bool caseSensitive)
    {
        if (count < needle.Length)
        {
            return -1;
        }

        // Case-sensitive search delegates to the vectorized span scan. The case-insensitive
        // path has to fold each byte, so it stays scalar, but it only probes positions whose
        // first byte already folds to the needle's.
        if (caseSensitive)
        {
            var idx = haystack.AsSpan(start, count).IndexOf(needle.AsSpan());
            return idx < 0 ? -1 : start + idx;
        }

        var end = start + count;
        var first = ToLowerAscii(needle[0]);
        for (int i = start; i + needle.Length <= end; i++)
        {
            if (ToLowerAscii(haystack[i]) != first)
            {
                continue;
            }
            var match = true;
            for (int j = 1; j < needle.Length; j++)
            {
                if (ToLowerAscii(haystack[i + j]) != ToLowerAscii(needle[j]))
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

    private static IEnumerable<HexSearchHit> SearchRegex(
        IHexBuffer buffer,
        HexSearchOptions options,
        long start,
        long end,
        CancellationToken ct)
    {
        var rxOpts = options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        Regex regex;
        try
        {
            regex = new Regex(options.Query, rxOpts | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException)
        {
            yield break;
        }

        // Regex runs over Latin-1-decoded windows (byte-for-char, so offsets stay exact).
        // Matches straddling a window boundary are not found — surfaced in the tool's help
        // text; narrow the range and re-run if a match is expected near a 1 MiB boundary.
        var bytes = new byte[ChunkSize];
        var pos = start;
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
                yield return new HexSearchHit(pos + m.Index, m.Length);
            }

            pos += read;
        }
    }
}
