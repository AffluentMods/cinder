using System.Buffers.Binary;

namespace Cinder.Carving;

/// <summary>
/// Random-access view of the input starting at one candidate object, handed to a
/// <see cref="CarveProbe"/>. Offsets are relative to the object's first byte.
///
/// <para>The first bytes come straight from the scan window (<see cref="Head"/>, at least
/// <see cref="FileCarver.HeadBytes"/> unless the input ends sooner). Anything further is read
/// from the source through a small read-ahead cache, so a probe that walks a structure —
/// MP4 boxes, pcap records — costs sequential I/O rather than a seek per field. A
/// non-seekable source can only serve what the window holds.</para>
/// </summary>
public sealed class CarveSource
{
    private const int CacheSize = 64 * 1024;

    private readonly byte[] _window;
    private readonly int _windowStart;
    private readonly int _inWindow;
    private readonly Stream? _stream;
    private readonly long _streamBase;

    private byte[]? _cache;
    private long _cacheOffset;
    private int _cacheLength;

    internal CarveSource(byte[] window, int windowStart, int inWindow, Stream? seekableStream, long streamBase, long available, long limit)
    {
        _window = window;
        _windowStart = windowStart;
        _inWindow = inWindow;
        _stream = seekableStream;
        _streamBase = streamBase;
        Available = available;
        Limit = limit;
    }

    /// <summary>Bytes from the object's start to the end of the readable input.</summary>
    public long Available { get; }

    /// <summary>The most this object may span: the signature's maximum, or <see cref="Available"/> if smaller.</summary>
    public long Limit { get; }

    /// <summary>The object's leading bytes, already in memory.</summary>
    public ReadOnlySpan<byte> Head => _window.AsSpan(_windowStart, _inWindow);

    /// <summary>
    /// Shared allowance for the work probes and footer searches may do, or null for no limit.
    /// See <see cref="FileCarver"/>: it keeps a source that is dense with headers leading
    /// nowhere from costing a long scan per header.
    /// </summary>
    internal ScanBudget? Budget { get; set; }

    /// <summary>True once a read was refused because <see cref="Budget"/> ran out.</summary>
    internal bool BudgetExhausted { get; private set; }

    private bool Charge(long bytes)
    {
        if (Budget is not { } budget)
        {
            return true;
        }
        if (budget.Remaining <= 0)
        {
            BudgetExhausted = true;
            return false;
        }
        budget.Remaining -= bytes + 16;     // the constant makes many tiny reads cost something too
        return true;
    }

    internal int InWindow => _inWindow;
    internal ReadOnlyMemory<byte> WindowBytes => _window.AsMemory(_windowStart, _inWindow);
    internal Stream? SeekableStream => _stream;
    internal long StreamBase => _streamBase;

    /// <summary>Reads up to <paramref name="destination"/>.Length bytes at <paramref name="offset"/>; returns the count read.</summary>
    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || !Charge(destination.Length))
        {
            return 0;
        }

        var total = 0;
        if (offset < _inWindow)
        {
            var n = (int)Math.Min(destination.Length, _inWindow - offset);
            _window.AsSpan(_windowStart + (int)offset, n).CopyTo(destination);
            total = n;
            offset += n;
            destination = destination[n..];
        }

        while (destination.Length > 0 && _stream is not null && offset < Available)
        {
            if (_cache is null || offset < _cacheOffset || offset >= _cacheOffset + _cacheLength)
            {
                _cache ??= new byte[CacheSize];
                _stream.Position = _streamBase + offset;
                var want = (int)Math.Min(CacheSize, Available - offset);
                var filled = 0;
                while (filled < want)
                {
                    var r = _stream.Read(_cache, filled, want - filled);
                    if (r <= 0)
                    {
                        break;
                    }
                    filled += r;
                }
                _cacheOffset = offset;
                _cacheLength = filled;
                if (filled == 0)
                {
                    break;
                }
            }

            var take = (int)Math.Min(destination.Length, _cacheOffset + _cacheLength - offset);
            _cache.AsSpan((int)(offset - _cacheOffset), take).CopyTo(destination);
            total += take;
            offset += take;
            destination = destination[take..];
        }
        return total;
    }

    /// <summary>Fills <paramref name="destination"/> completely from <paramref name="offset"/>, or returns false.</summary>
    public bool TryRead(long offset, Span<byte> destination) => Read(offset, destination) == destination.Length;

    public bool TryReadUInt16LittleEndian(long offset, out ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        var ok = TryRead(offset, b);
        value = ok ? BinaryPrimitives.ReadUInt16LittleEndian(b) : (ushort)0;
        return ok;
    }

    public bool TryReadUInt32LittleEndian(long offset, out uint value)
    {
        Span<byte> b = stackalloc byte[4];
        var ok = TryRead(offset, b);
        value = ok ? BinaryPrimitives.ReadUInt32LittleEndian(b) : 0;
        return ok;
    }

    public bool TryReadUInt32BigEndian(long offset, out uint value)
    {
        Span<byte> b = stackalloc byte[4];
        var ok = TryRead(offset, b);
        value = ok ? BinaryPrimitives.ReadUInt32BigEndian(b) : 0;
        return ok;
    }

    /// <summary>
    /// Offset of the first occurrence of <paramref name="pattern"/> that starts at or after
    /// <paramref name="from"/> and ends at or before <paramref name="limit"/>; -1 if none.
    /// </summary>
    public long IndexOf(ReadOnlySpan<byte> pattern, long from, long limit, CancellationToken ct = default)
    {
        if (pattern.Length == 0 || from < 0)
        {
            return -1;
        }
        limit = Math.Min(limit, Available);

        // The part already in memory is searched in place.
        if (from < _inWindow)
        {
            var end = (int)Math.Min(_inWindow, limit);
            if (end - from >= pattern.Length && Charge(end - from))
            {
                var idx = _window.AsSpan(_windowStart + (int)from, end - (int)from).IndexOf(pattern);
                if (idx >= 0)
                {
                    return from + idx;
                }
            }
            // Resume so that a match straddling the end of the window is still seen.
            from = Math.Max(from, _inWindow - (pattern.Length - 1));
        }

        if (_stream is null)
        {
            return -1;
        }

        var scratch = new byte[CacheSize];
        var carry = 0;   // bytes kept from the previous block so a split match is found
        var pos = from;
        while (pos + carry < limit)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(scratch.Length - carry, limit - (pos + carry));
            var got = Read(pos + carry, scratch.AsSpan(carry, want));
            if (got <= 0)
            {
                break;
            }
            var have = carry + got;
            var idx = scratch.AsSpan(0, have).IndexOf(pattern);
            if (idx >= 0)
            {
                return pos + idx;
            }
            var keep = Math.Min(pattern.Length - 1, have);
            scratch.AsSpan(have - keep, keep).CopyTo(scratch);
            pos += have - keep;
            carry = keep;
        }
        return -1;
    }
}

/// <summary>Work allowance shared by every <see cref="CarveSource"/> of one carve.</summary>
internal sealed class ScanBudget
{
    public long Remaining { get; set; }
}
