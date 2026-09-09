namespace Cinder.Imaging.Ewf;

/// <summary>
/// Seekable read-only <see cref="Stream"/> over the raw disk exposed by an
/// <see cref="EwfReader"/>. Caches one chunk at a time — reads spanning chunks
/// concatenate transparently. Thread-unsafe by design; create a per-reader stream
/// or wrap accesses externally.
///
/// <para>The stream always yields exactly <see cref="Length"/> bytes. A chunk that fails to
/// decode is zero-filled by <see cref="EwfReader"/> and counted in
/// <see cref="EwfReader.DamagedChunks"/>, never shortened: a short read here would be
/// indistinguishable from end-of-stream to a hasher or a carver, which would then report a
/// clean result over a partially-read image.</para>
/// </summary>
public sealed class EwfStream : Stream
{
    private readonly EwfReader _reader;
    private long _position;
    private byte[]? _cachedChunkData;
    private int _cachedChunkIndex = -1;

    public EwfStream(EwfReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
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

            // The media size comes from the volume section and the chunk index from the table
            // sections; nothing in the format forces them to agree. When they don't, say so
            // rather than letting an out-of-range index escape as an ArgumentOutOfRangeException
            // from somewhere deeper.
            if (chunkIndex >= _reader.ChunkCount)
            {
                throw new InvalidDataException(
                    $"EWF: media size implies at least {chunkIndex + 1:N0} chunks but the chunk table " +
                    $"holds {_reader.ChunkCount:N0}. The container's volume and table sections disagree.");
            }

            var chunk = LoadChunk(chunkIndex);

            var availableInChunk = chunk.Length - offsetInChunk;
            if (availableInChunk <= 0)
            {
                // The chunk decoded shorter than its geometry says it should. EwfReader
                // zero-fills to the logical length, so reaching here means the index and the
                // volume geometry disagree — a structural defect, not an end-of-media. Report
                // it rather than returning a short read that reads as a clean EOF.
                throw new InvalidDataException(
                    $"EWF: chunk {chunkIndex} decoded to {chunk.Length} bytes but the media geometry " +
                    $"requires at least {offsetInChunk + 1}. The container's volume and table sections disagree.");
            }

            int copyable = (int)Math.Min(availableInChunk, remaining);
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
