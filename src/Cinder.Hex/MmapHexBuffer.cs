using System.IO.MemoryMappedFiles;

namespace Cinder.Hex;

/// <summary>
/// Memory-mapped hex buffer. Backed by a single <see cref="MemoryMappedFile"/> view created on
/// open; reads are O(1) random-access without paging anything into managed memory until touched.
/// This is the buffer Cinder uses for evidence images.
/// </summary>
public sealed class MmapHexBuffer : IHexBuffer
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly FileStream _file;

    public string DisplayName { get; }
    public long Length { get; }
    public bool IsReadOnly => true;

    public MmapHexBuffer(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Length = _file.Length;
        DisplayName = Path.GetFileName(path);

        if (Length == 0)
        {
            _mmf = null!;
            _accessor = null!;
            return;
        }

        _mmf = MemoryMappedFile.CreateFromFile(_file, mapName: null, capacity: Length,
            MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        _accessor = _mmf.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);
    }

    public int Read(long offset, Span<byte> destination)
    {
        if (Length == 0 || offset >= Length)
        {
            return 0;
        }

        var available = (int)Math.Min(destination.Length, Length - offset);
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                new ReadOnlySpan<byte>(ptr + offset, available).CopyTo(destination);
            }
            finally
            {
                if (ptr is not null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
        return available;
    }

    public void Dispose()
    {
        _accessor?.Dispose();
        _mmf?.Dispose();
        _file.Dispose();
    }
}
