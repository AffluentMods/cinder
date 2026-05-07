namespace Cinder.Hex;

/// <summary>In-memory <see cref="IHexBuffer"/>. Used for tests and small fixture data.</summary>
public sealed class ByteArrayHexBuffer : IHexBuffer
{
    private readonly byte[] _bytes;
    public string DisplayName { get; }
    public long Length => _bytes.Length;
    public bool IsReadOnly => true;

    public ByteArrayHexBuffer(byte[] bytes, string displayName = "(buffer)")
    {
        _bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        DisplayName = displayName;
    }

    public int Read(long offset, Span<byte> destination)
    {
        if (offset >= _bytes.Length)
        {
            return 0;
        }
        var available = (int)Math.Min(destination.Length, _bytes.Length - offset);
        _bytes.AsSpan((int)offset, available).CopyTo(destination);
        return available;
    }

    public void Dispose() { }
}
