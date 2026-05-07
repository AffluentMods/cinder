namespace Cinder.Hex;

/// <summary>
/// Random-access byte source backing the hex viewer. Implementations may be in-memory, mmap-backed,
/// or stream-backed. Reads are synchronous because the viewer renders on the UI thread and must
/// produce visible rows in O(milliseconds); large files should use mmap to keep that contract.
/// </summary>
public interface IHexBuffer : IDisposable
{
    long Length { get; }
    string DisplayName { get; }
    bool IsReadOnly { get; }

    int Read(long offset, Span<byte> destination);
}
