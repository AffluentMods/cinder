namespace Cinder.Native;

/// <summary>A read-only handle to a raw storage device. Disposed via the OS handle.</summary>
public interface IRawDevice : IDisposable
{
    string Identifier { get; }
    long? SizeBytes { get; }
    int SectorSize { get; }

    /// <summary>Read <paramref name="buffer"/> bytes starting at <paramref name="offset"/>.</summary>
    int Read(long offset, Span<byte> buffer);
}
