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
