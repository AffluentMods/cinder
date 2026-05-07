using System.Buffers;
using System.Collections.Concurrent;

namespace Cinder.Carving;

/// <summary>One carved blob: source offset, length, signature, validation result.</summary>
public sealed record CarveHit(string Label, string Extension, long Offset, long Length, bool Validated, string? OutputPath = null);

/// <summary>
/// Header/footer file carver with optional smart validation. Reads the input stream in 4 MiB
/// chunks with a tail overlap = max-header-length so cross-chunk hits aren't missed. Each hit is
/// extracted by reading from the source between header and either footer or max-length, then
/// optionally fed to the signature's validator before being written.
/// </summary>
public sealed class FileCarver
{
    private const int ChunkSize = 4 << 20;

    private readonly IReadOnlyList<CarveSignature> _signatures;
    private readonly int _maxHeaderLen;

    public FileCarver(IReadOnlyList<CarveSignature>? signatures = null)
    {
        _signatures = signatures ?? CarveSignatures.Defaults;
        _maxHeaderLen = _signatures.Max(s => s.Header.Length);
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
        var buffer = pool.Rent(ChunkSize + _maxHeaderLen);
        long pos = 0;
        int retained = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer.AsMemory(retained, ChunkSize), ct).ConfigureAwait(false);
                if (read == 0 && retained == 0)
                {
                    yield break;
                }
                var len = retained + read;

                foreach (var hit in ScanChunk(buffer, len, pos))
                {
                    var blob = await ExtractBlobAsync(input, hit, ct).ConfigureAwait(false);
                    var validated = hit.Signature.Validator?.Invoke(blob) ?? true;
                    string? outPath = null;
                    if (validated && outputDirectory is not null)
                    {
                        outPath = Path.Combine(outputDirectory,
                            $"{hit.AbsoluteOffset:X12}.{hit.Signature.Extension}");
                        await File.WriteAllBytesAsync(outPath, blob.ToArray(), ct).ConfigureAwait(false);
                    }
                    yield return new CarveHit(hit.Signature.Label, hit.Signature.Extension,
                        hit.AbsoluteOffset, blob.Length, validated, outPath);
                }

                if (read == 0)
                {
                    yield break;
                }
                pos += len - _maxHeaderLen;
                Array.Copy(buffer, len - _maxHeaderLen, buffer, 0, _maxHeaderLen);
                retained = _maxHeaderLen;
                progress?.Report(pos);
            }
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    private IEnumerable<RawHit> ScanChunk(byte[] buf, int length, long basePos)
    {
        var bag = new ConcurrentBag<RawHit>();
        Parallel.ForEach(_signatures, sig =>
        {
            for (int i = 0; i + sig.Header.Length <= length; i++)
            {
                if (Matches(buf, i, sig.Header))
                {
                    bag.Add(new RawHit(sig, i, basePos + i));
                }
            }
        });
        return bag.OrderBy(h => h.LocalOffset);
    }

    private static bool Matches(byte[] buf, int start, byte[] needle)
    {
        for (int j = 0; j < needle.Length; j++)
        {
            if (buf[start + j] != needle[j])
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<ReadOnlyMemory<byte>> ExtractBlobAsync(Stream input, RawHit hit, CancellationToken ct)
    {
        if (!input.CanSeek)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        var savedPos = input.Position;
        input.Position = hit.AbsoluteOffset;
        var max = (int)Math.Min(hit.Signature.MaxLengthBytes, input.Length - hit.AbsoluteOffset);
        var buf = new byte[max];
        var n = await input.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
        input.Position = savedPos;
        if (n <= 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var actual = n;
        var footer = hit.Signature.Footer;
        if (footer is { Length: > 0 })
        {
            var idx = buf.AsSpan(hit.Signature.Header.Length, n - hit.Signature.Header.Length).IndexOf(footer);
            if (idx >= 0)
            {
                actual = hit.Signature.Header.Length + idx + footer.Length;
            }
        }
        return new ReadOnlyMemory<byte>(buf, 0, actual);
    }

    private sealed record RawHit(CarveSignature Signature, int LocalOffset, long AbsoluteOffset);
}
