using System.Buffers;
using System.Collections.Concurrent;

namespace Cinder.Carving;

/// <summary>One carved blob: source offset, length, signature, validation result.</summary>
public sealed record CarveHit(string Label, string Extension, long Offset, long Length, bool Validated, string? OutputPath = null);

/// <summary>
/// Header/footer file carver.
///
/// <para>Reads the input in 4 MiB windows with a tail overlap of <c>maxHeaderLength - 1</c> so a
/// header straddling a window boundary is still matched. Each window "owns" the region before
/// its overlap tail; hits inside the tail are left for the next window, which sees them at its
/// head. That partition is what keeps a boundary-straddling header from being carved twice.</para>
///
/// <para>Each hit is extracted from the window when it fits there, or by seeking the source
/// when it doesn't, then trimmed at the signature's footer and optionally passed to the
/// signature's validator before being written.</para>
/// </summary>
public sealed class FileCarver
{
    private const int ChunkSize = 4 << 20;

    private readonly IReadOnlyList<CarveSignature> _signatures;
    private readonly int _overlap;

    public FileCarver(IReadOnlyList<CarveSignature>? signatures = null)
    {
        _signatures = signatures ?? CarveSignatures.Defaults;
        if (_signatures.Count == 0)
        {
            throw new ArgumentException("At least one carve signature is required.", nameof(signatures));
        }
        foreach (var s in _signatures)
        {
            if (s.Header.Length == 0)
            {
                throw new ArgumentException($"Carve signature '{s.Label}' has an empty header pattern.", nameof(signatures));
            }
        }
        _overlap = _signatures.Max(s => s.Header.Length) - 1;
    }

    public async IAsyncEnumerable<CarveHit> CarveAsync(
        Stream input,
        string? outputDirectory = null,
        IProgress<long>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var pool = ArrayPool<byte>.Shared;
        var window = pool.Rent(ChunkSize + _overlap);
        long windowStart = 0;
        var retained = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var read = await ReadFullAsync(input, window.AsMemory(retained, ChunkSize), ct).ConfigureAwait(false);
                var len = retained + read;
                if (len == 0)
                {
                    yield break;
                }

                // A window that couldn't be filled is the last one, and owns its whole extent.
                var isLast = read < ChunkSize;
                var ownedEnd = isLast ? len : len - _overlap;

                foreach (var raw in ScanWindow(window, len, ownedEnd))
                {
                    ct.ThrowIfCancellationRequested();

                    var absolute = windowStart + raw.LocalOffset;
                    var blob = await ExtractBlobAsync(input, window, len, raw, absolute, ct).ConfigureAwait(false);
                    if (blob.Length == 0)
                    {
                        continue;
                    }

                    var validated = raw.Signature.Validator?.Invoke(blob) ?? true;
                    string? outPath = null;
                    if (validated && outputDirectory is not null)
                    {
                        outPath = Path.Combine(outputDirectory, $"{absolute:X12}.{raw.Signature.Extension}");
                        await WriteBlobAsync(outPath, blob, ct).ConfigureAwait(false);
                    }

                    yield return new CarveHit(
                        raw.Signature.Label, raw.Signature.Extension, absolute, blob.Length, validated, outPath);
                }

                progress?.Report(windowStart + ownedEnd);

                if (isLast)
                {
                    yield break;
                }

                Array.Copy(window, len - _overlap, window, 0, _overlap);
                windowStart += len - _overlap;
                retained = _overlap;
            }
        }
        finally
        {
            pool.Return(window);
        }
    }

    /// <summary>
    /// Finds every signature header in <c>window[0..length)</c> that starts before
    /// <paramref name="ownedEnd"/>, in ascending offset order.
    /// </summary>
    private List<RawHit> ScanWindow(byte[] window, int length, int ownedEnd)
    {
        var bag = new ConcurrentBag<RawHit>();

        Parallel.ForEach(_signatures, sig =>
        {
            var span = window.AsSpan(0, length);
            var from = 0;
            while (from < ownedEnd)
            {
                // Vectorized. The previous scalar byte-by-byte comparison ran roughly
                // length × signatures × headerLength operations per window, which made a
                // whole-disk carve impractically slow.
                var idx = span[from..].IndexOf(sig.Header.AsSpan());
                if (idx < 0)
                {
                    break;
                }
                var at = from + idx;
                if (at >= ownedEnd)
                {
                    break;
                }
                bag.Add(new RawHit(sig, at));
                from = at + 1;
            }
        });

        var hits = bag.ToList();
        hits.Sort(static (a, b) => a.LocalOffset.CompareTo(b.LocalOffset));
        return hits;
    }

    /// <summary>
    /// Materializes the bytes for one hit, trimmed at the signature's footer if it has one.
    /// Prefers the in-memory window; falls back to seeking the source for objects that extend
    /// past it. For a non-seekable source that can't be reached, carves what the window holds
    /// rather than returning nothing.
    /// </summary>
    private static async Task<ReadOnlyMemory<byte>> ExtractBlobAsync(
        Stream input,
        byte[] window,
        int windowLength,
        RawHit hit,
        long absoluteOffset,
        CancellationToken ct)
    {
        var sig = hit.Signature;
        var wantedFromWindow = windowLength - hit.LocalOffset;
        var maxWanted = sig.MaxLengthBytes;

        // Fast path: the object's whole permitted extent is already in the window.
        if (wantedFromWindow >= maxWanted)
        {
            var slice = new ReadOnlyMemory<byte>(window, hit.LocalOffset, (int)maxWanted);
            return TrimToFooter(slice, sig);
        }

        if (input.CanSeek)
        {
            var savedPos = input.Position;
            try
            {
                var available = input.Length - absoluteOffset;
                if (available <= 0)
                {
                    return ReadOnlyMemory<byte>.Empty;
                }

                var max = (int)Math.Min(maxWanted, available);
                var buf = new byte[max];
                input.Position = absoluteOffset;
                var n = await ReadFullAsync(input, buf.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    return ReadOnlyMemory<byte>.Empty;
                }
                return TrimToFooter(new ReadOnlyMemory<byte>(buf, 0, n), sig);
            }
            finally
            {
                input.Position = savedPos;
            }
        }

        // Non-seekable source: carve what this window holds. Previously this returned an empty
        // blob, which meant every hit found through SlackUnallocCarver's non-seekable slice
        // was reported with length 0 and never written.
        var partial = new ReadOnlyMemory<byte>(window, hit.LocalOffset, wantedFromWindow);
        return TrimToFooter(partial, sig);
    }

    /// <summary>Cuts the blob at the first footer occurrence after the header, if one is found.</summary>
    private static ReadOnlyMemory<byte> TrimToFooter(ReadOnlyMemory<byte> blob, CarveSignature sig)
    {
        var footer = sig.Footer;
        if (footer is not { Length: > 0 } || blob.Length <= sig.Header.Length)
        {
            return blob;
        }

        var searchFrom = sig.Header.Length;
        var idx = blob.Span[searchFrom..].IndexOf(footer.AsSpan());
        if (idx < 0)
        {
            return blob;
        }
        return blob[..(searchFrom + idx + footer.Length)];
    }

    private static async Task WriteBlobAsync(string path, ReadOnlyMemory<byte> blob, CancellationToken ct)
    {
        await using var fs = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        await fs.WriteAsync(blob, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads until <paramref name="destination"/> is full or the stream ends, returning the byte
    /// count. Streams may legitimately return short reads; treating one as end-of-input would
    /// mis-size the window and shift every subsequent offset.
    /// </summary>
    private static async Task<int> ReadFullAsync(Stream input, Memory<byte> destination, CancellationToken ct)
    {
        var filled = 0;
        while (filled < destination.Length)
        {
            var n = await input.ReadAsync(destination[filled..], ct).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }
            filled += n;
        }
        return filled;
    }

    private sealed record RawHit(CarveSignature Signature, int LocalOffset);
}
