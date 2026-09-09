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

    /// <summary>
    /// Stream wrapper that exposes a fixed slice of an underlying stream.
    ///
    /// <para>Seekable whenever the underlying image is. That matters: the carver falls back to
    /// window-bounded extraction on a non-seekable source, so a slice that refused to seek
    /// capped every carved object at the current 4 MiB window. Positions are slice-relative
    /// and every read re-seats the underlying stream, so interleaved use is safe.</para>
    /// </summary>
    private sealed class SubStream(Stream inner, long length) : Stream
    {
        private readonly Stream _inner = inner;
        private readonly long _start = inner.Position;
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _read;
            set
            {
                if (!CanSeek)
                {
                    throw new NotSupportedException();
                }
                if (value < 0 || value > length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                _read = value;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _read + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return _read;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = length - _read;
            if (remaining <= 0)
            {
                return 0;
            }
            if (_inner.CanSeek)
            {
                _inner.Position = _start + _read;
            }
            var n = _inner.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
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
            if (_inner.CanSeek)
            {
                _inner.Position = _start + _read;
            }
            var n = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], ct).ConfigureAwait(false);
            _read += n;
            return n;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
