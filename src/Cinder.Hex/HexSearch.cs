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
/// Streaming search over an <see cref="IHexBuffer"/>. Operates in 1 MiB chunks with an overlap
/// equal to the search pattern length so cross-chunk matches aren't missed. Yields hits as it
/// finds them so the UI can render them progressively.
/// </summary>
public static class HexSearch
{
    private const int ChunkSize = 1 << 20;

    public static IEnumerable<HexSearchHit> Search(IHexBuffer buffer, HexSearchOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);

        var pattern = BuildPattern(options);
        if (pattern.Length == 0)
        {
            yield break;
        }

        var end = options.EndOffset ?? buffer.Length;
        if (end > buffer.Length)
        {
            end = buffer.Length;
        }

        if (options.Kind == HexSearchKind.Regex)
        {
            foreach (var hit in SearchRegex(buffer, options, end, ct))
            {
                yield return hit;
            }
            yield break;
        }

        var overlap = pattern.Length - 1;
        var bufRented = new byte[ChunkSize + overlap];
        long pos = options.StartOffset;

        while (pos < end)
        {
            ct.ThrowIfCancellationRequested();

            var want = (int)Math.Min(bufRented.Length, end - pos);
            var read = buffer.Read(pos, bufRented.AsSpan(0, want));
            if (read == 0)
            {
                yield break;
            }

            var local = 0;
            while (true)
            {
                var idx = IndexOfArray(bufRented, local, read - local, pattern, options.CaseSensitive);
                if (idx < 0)
                {
                    break;
                }
                yield return new HexSearchHit(pos + idx, pattern.Length);
                local = idx + 1;
            }

            if (read < want)
            {
                yield break;
            }
            pos += read - overlap;
        }
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
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        Span<char> stripped = stackalloc char[query.Length];
        var n = 0;
        foreach (var c in query)
        {
            if (char.IsWhiteSpace(c) || c == '-' || c == ':' || c == ',')
            {
                continue;
            }
            stripped[n++] = c;
        }
        var src = stripped[..n];

        if (n % 2 != 0)
        {
            return [];
        }

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

    /// <summary>Index-based array search returning the absolute index into <paramref name="haystack"/>, or -1.</summary>
    private static int IndexOfArray(byte[] haystack, int start, int count, byte[] needle, bool caseSensitive)
    {
        var end = start + count;
        for (int i = start; i + needle.Length <= end; i++)
        {
            var match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                var a = haystack[i + j];
                var b = needle[j];
                if (!caseSensitive)
                {
                    a = ToLowerAscii(a);
                    b = ToLowerAscii(b);
                }
                if (a != b)
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

    private static IEnumerable<HexSearchHit> SearchRegex(IHexBuffer buffer, HexSearchOptions options, long end, CancellationToken ct)
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

        // Regex search runs over ASCII-decoded chunks; for binary regex, callers should escape.
        var bytes = new byte[ChunkSize];
        long pos = options.StartOffset;
        while (pos < end)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(bytes.Length, end - pos);
            var read = buffer.Read(pos, bytes.AsSpan(0, want));
            if (read == 0)
            {
                yield break;
            }
            var text = Encoding.Latin1.GetString(bytes, 0, read);
            foreach (Match m in regex.Matches(text))
            {
                yield return new HexSearchHit(pos + m.Index, m.Length);
            }
            if (read < want)
            {
                yield break;
            }
            pos += read;
        }
    }
}
