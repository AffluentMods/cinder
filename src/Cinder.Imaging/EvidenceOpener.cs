using Cinder.Imaging.Ewf;

namespace Cinder.Imaging;

/// <summary>
/// One-shot opener for any evidence file Cinder might ingest. Auto-detects EWF (.E01)
/// containers and returns a transparent <see cref="EwfStream"/> over the raw disk;
/// for every other path it returns a plain read-only <see cref="FileStream"/>.
///
/// Use this from any tool that wants "give me bytes" without caring whether the user
/// dropped a raw .dd, an .E01, or anything else file-shaped. The carver, YARA scanner,
/// imager-verify, and signature scanner all consume the result the same way.
/// </summary>
public static class EvidenceOpener
{
    /// <summary>
    /// Opens <paramref name="path"/> as a read-only seekable stream. Caller owns the
    /// returned stream — dispose it (or wrap in <c>using</c>) when finished.
    /// </summary>
    public static Stream Open(string path)
    {
        // Sniff first 8 bytes; if EVF magic, hand back the EWF-backed Stream.
        // Otherwise it's a raw file — return a FileStream so callers see byte-for-byte.
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (IsAff4Magic(fs))
        {
            fs.Dispose();
            return Aff4.Aff4Reader.Open(path).OpenOwningStream();
        }
        var vd = VirtualDiskKind(fs);
        if (vd is not null)
        {
            // VHD / VHDX: hand back the disk contents, not the container bytes, so hashes,
            // carving and filesystem parsing see the same media a mount would.
            DiscUtils.VirtualDisk disk = vd == "vhdx"
                ? new DiscUtils.Vhdx.Disk(fs, DiscUtils.Streams.Ownership.Dispose)
                : new DiscUtils.Vhd.Disk(fs, DiscUtils.Streams.Ownership.Dispose);
            return new VirtualDiskDisposingStream(disk);
        }
        if (IsEwfMagic(fs))
        {
            try
            {
                // EwfReader.Open() globs sibling segments; we need to use that path so
                // multi-segment chains work even when the caller hands us only the .E01.
                fs.Dispose();
                var reader = EwfReader.Open(path);
                return new EwfDisposingStream(reader);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }
        return fs;
    }

    /// <summary>True for an AFF4 container (a ZIP volume with a <c>container.description</c>).</summary>
    public static bool IsAff4(string path) => Aff4.Aff4Reader.IsAff4(path);

    /// <summary>"vhd", "vhdx", or null. Detected by magic, not extension, so a renamed file still opens as a disk.</summary>
    public static string? VirtualDiskKind(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return VirtualDiskKind(fs);
        }
        catch { return null; }
    }

    private static string? VirtualDiskKind(FileStream s)
    {
        var save = s.Position;
        try
        {
            if (s.Length < 512)
            {
                return null;
            }
            Span<byte> head = stackalloc byte[8];
            s.Position = 0;
            if (s.Read(head) == 8)
            {
                if (head.SequenceEqual("vhdxfile"u8)) return "vhdx";
                if (head.SequenceEqual("conectix"u8)) return "vhd";     // dynamic / differencing: footer copy up front
            }
            // Fixed VHD: only the trailing footer carries the cookie.
            s.Position = s.Length - 512;
            if (s.Read(head) == 8 && head.SequenceEqual("conectix"u8))
            {
                return "vhd";
            }
            return null;
        }
        catch (IOException) { return null; }
        finally
        {
            s.Position = save;
        }
    }

    /// <summary>Content stream of a virtual disk that tears the disk (and its file) down on dispose.</summary>
    private sealed class VirtualDiskDisposingStream(DiscUtils.VirtualDisk disk) : Stream
    {
        private readonly Stream _inner = disk.Content;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disk.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private static bool IsAff4Magic(FileStream s)
    {
        var save = s.Position;
        try
        {
            s.Position = 0;
            Span<byte> head = stackalloc byte[4];
            if (s.Read(head) < 4 || head[0] != 'P' || head[1] != 'K' || head[2] != 3 || head[3] != 4)
            {
                return false;
            }
            s.Position = 0;
            using var zip = new System.IO.Compression.ZipArchive(s, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
            return zip.GetEntry("container.description") is not null || zip.GetEntry("information.turtle") is not null;
        }
        catch (InvalidDataException) { return false; }
        finally
        {
            s.Position = save;
        }
    }

    public static bool IsEwf(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return IsEwfMagic(fs);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEwfMagic(Stream s)
    {
        if (s.Length < 8) return false;
        var save = s.Position;
        try
        {
            s.Position = 0;
            Span<byte> head = stackalloc byte[8];
            int filled = 0;
            while (filled < 8)
            {
                int r = s.Read(head[filled..]);
                if (r <= 0) break;
                filled += r;
            }
            return filled == 8 && head.SequenceEqual(EwfReader.Magic);
        }
        finally
        {
            s.Position = save;
        }
    }

    /// <summary>
    /// EwfStream alone doesn't own the underlying <see cref="EwfReader"/> — wrap it so
    /// the caller's `using` properly tears down both the stream and the open segment files.
    /// </summary>
    private sealed class EwfDisposingStream : Stream
    {
        private readonly EwfReader _owner;
        private readonly Stream _inner;

        public EwfDisposingStream(EwfReader owner)
        {
            _owner = owner;
            _inner = owner.OpenStream();
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
