namespace Cinder.Imaging.Ewf;

/// <summary>
/// Seekable read-only <see cref="Stream"/> over the raw disk exposed by an
/// <see cref="EwfReader"/>. Caches one chunk at a time — reads spanning chunks
/// concatenate transparently. Thread-unsafe by design; create a per-reader stream
/// or wrap accesses externally.
/// </summary>
public sealed class EwfStream : Stream
{
    private readonly EwfReader _reader;
    private long _position;
    private byte[]? _cachedChunkData;
    private int _cachedChunkIndex = -1;

    public EwfStream(EwfReader reader)
    {
        _reader = reader;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _reader.MediaSize;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > Length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _position = value;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= Length || buffer.Length == 0)
        {
            return 0;
        }
        int chunkSize = _reader.ChunkSize;
        int totalCopied = 0;
        long remaining = Math.Min(buffer.Length, Length - _position);

        while (remaining > 0)
        {
            int chunkIndex = checked((int)(_position / chunkSize));
            int offsetInChunk = checked((int)(_position % chunkSize));
            var chunk = LoadChunk(chunkIndex);
            int copyable = Math.Min(chunk.Length - offsetInChunk, (int)Math.Min(remaining, int.MaxValue));
            if (copyable <= 0) break;
            chunk.AsSpan(offsetInChunk, copyable).CopyTo(buffer[totalCopied..]);
            totalCopied += copyable;
            _position += copyable;
            remaining -= copyable;
        }
        return totalCopied;
    }

    private byte[] LoadChunk(int index)
    {
        if (_cachedChunkIndex == index && _cachedChunkData is not null)
        {
            return _cachedChunkData;
        }
        _cachedChunkData = _reader.ReadChunk(index);
        _cachedChunkIndex = index;
        return _cachedChunkData;
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
