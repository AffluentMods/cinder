namespace Cinder.Carving;

/// <summary>
/// Carves slack and unallocated regions reported by the filesystem parser. The pytsk3 sidecar
/// emits offset+length tuples for every slack/unalloc cluster; this class iterates them and
/// runs the standard <see cref="FileCarver"/> on each.
/// </summary>
public sealed record CarveRegion(string Source, long Offset, long Length);

public sealed class SlackUnallocCarver(FileCarver carver)
{
    private readonly FileCarver _carver = carver ?? throw new ArgumentNullException(nameof(carver));

    public async IAsyncEnumerable<CarveHit> CarveRegionsAsync(
        Stream image,
        IEnumerable<CarveRegion> regions,
        string? outputDirectory = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var region in regions)
        {
            ct.ThrowIfCancellationRequested();
            image.Position = region.Offset;
            var slice = new SubStream(image, region.Length);
            await foreach (var hit in _carver.CarveAsync(slice, outputDirectory, null, ct).ConfigureAwait(false))
            {
                yield return hit with { Offset = region.Offset + hit.Offset };
            }
        }
    }

    /// <summary>Stream wrapper that exposes a fixed slice of an underlying stream.</summary>
    private sealed class SubStream(Stream inner, long length) : Stream
    {
        private readonly Stream _inner = inner;
        private readonly long _start = inner.Position;
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - _read;
            if (remaining <= 0)
            {
                return 0;
            }
            var n = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            _read += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var remaining = length - _read;
            if (remaining <= 0)
            {
                return 0;
            }
            var n = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], ct).ConfigureAwait(false);
            _read += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
