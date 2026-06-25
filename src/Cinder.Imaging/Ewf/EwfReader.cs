using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace Cinder.Imaging.Ewf;

/// <summary>
/// In-process reader for the EnCase / FTK EWF (Expert Witness Format, .E01) container.
///
/// Supports single-segment .E01 files: parses the EVF magic + section chain (header2 /
/// volume / sectors / table / done) and exposes the underlying raw disk as a seekable
/// <see cref="Stream"/> via <see cref="OpenStream"/>. Per-chunk on-demand decompression
/// uses ZLib (RFC 1950) via <see cref="ZLibStream"/>.
///
/// Scope today:
///   - single-segment .E01 only (multi-segment .E02/.E03 chains pending)
///   - first volume section's geometry
///   - no integrity-check verification against the recorded MD5 / SHA-1
///
/// Reference: ASR Data's "Expert Witness Compression Format Specification v0.1.5"
/// and the libewf source.
/// </summary>
public sealed class EwfReader : IDisposable
{
    // E V F  \t  \r  \n  0xFF 0x00 — the eight-byte EVF magic prefix.
    public static ReadOnlySpan<byte> Magic => new byte[] { 0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00 };

    private readonly Stream _file;
    private readonly bool _ownsStream;

    public string? CaseDescription { get; private set; }
    public string? AcquisitionDate { get; private set; }
    public uint BytesPerSector { get; private set; }
    public uint SectorsPerChunk { get; private set; }
    public ulong NumberOfChunks { get; private set; }
    public ulong NumberOfSectors { get; private set; }
    public long MediaSize => checked((long)(NumberOfSectors * BytesPerSector));
    public int ChunkSize => checked((int)(SectorsPerChunk * BytesPerSector));
    public string? RecordedMd5 { get; private set; }
    public string? RecordedSha1 { get; private set; }

    /// <summary>Compressed-chunk offsets in physical file order. Bit 31 set ⇒ compressed.</summary>
    internal long[] ChunkOffsets { get; private set; } = [];
    /// <summary>Size of each compressed chunk in bytes; the final chunk's compressed size is special-cased.</summary>
    internal long[] ChunkPhysicalSizes { get; private set; } = [];

    public EwfReader(Stream stream, bool ownsStream = false)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("EWF reader requires a seekable stream.", nameof(stream));
        }
        _file = stream;
        _ownsStream = ownsStream;
        ParseHeader();
        ParseSections();
    }

    public static EwfReader Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return new EwfReader(fs, ownsStream: true);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private void ParseHeader()
    {
        _file.Position = 0;
        Span<byte> hdr = stackalloc byte[13];
        if (_file.Read(hdr) != hdr.Length)
        {
            throw new InvalidDataException("EWF: file too short for header.");
        }
        if (!hdr[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException("EWF: bad magic (expected EVF\\x09\\x0d\\x0a\\xff\\x00).");
        }
    }

    private void ParseSections()
    {
        var tableOffsets = new List<List<long>>();
        var tableBases = new List<long>();   // base offset (in file) for each table's per-chunk offsets
        long pos = 13;
        var chunks = new List<long>();
        var sizes = new List<long>();

        while (true)
        {
            _file.Position = pos;
            var hdr = ReadExact(76);
            var type = ReadCString(hdr.AsSpan(0, 16));
            var next = BitConverter.ToInt64(hdr, 16);
            var size = BitConverter.ToInt64(hdr, 24);
            // hdr[32..72] = reserved, hdr[72..76] = adler32

            var dataStart = pos + 76;
            var dataLen = checked(size - 76);

            switch (type)
            {
                case "header2":
                case "header":
                    if (CaseDescription is null && dataLen > 0)
                    {
                        var raw = ReadAt(dataStart, (int)dataLen);
                        try { CaseDescription = DecompressString(raw, type == "header2"); }
                        catch { /* tolerate broken header */ }
                    }
                    break;

                case "disk":
                case "volume":
                    if (dataLen >= 32)
                    {
                        var v = ReadAt(dataStart, (int)dataLen);
                        var chunkCount = BitConverter.ToUInt32(v, 4);
                        var sectorsPerChunk = BitConverter.ToUInt32(v, 8);
                        var bytesPerSector = BitConverter.ToUInt32(v, 12);
                        var sectorCount = BitConverter.ToUInt32(v, 16);
                        NumberOfChunks = chunkCount;
                        SectorsPerChunk = sectorsPerChunk;
                        BytesPerSector = bytesPerSector;
                        NumberOfSectors = sectorCount;
                    }
                    break;

                case "table":
                    if (dataLen >= 24)
                    {
                        var t = ReadAt(dataStart, (int)dataLen);
                        var entryCount = BitConverter.ToUInt32(t, 0);
                        var tableBase = BitConverter.ToUInt64(t, 8);   // base offset to add to each entry
                        // 4 bytes: reserved at 4..8
                        // 16 bytes: reserved at 16..32
                        // entries start at offset 24 of the section data — each is uint32
                        var entries = new List<long>(checked((int)entryCount));
                        for (long i = 0; i < entryCount; i++)
                        {
                            var off = BitConverter.ToUInt32(t, 24 + (int)(i * 4));
                            entries.Add(off);
                        }
                        tableOffsets.Add(entries);
                        tableBases.Add((long)tableBase);
                    }
                    break;

                case "hash":
                    if (dataLen >= 20)
                    {
                        var h = ReadAt(dataStart, (int)dataLen);
                        RecordedMd5 = Convert.ToHexString(h.AsSpan(0, 16)).ToLowerInvariant();
                    }
                    break;

                case "digest":
                    if (dataLen >= 36)
                    {
                        var h = ReadAt(dataStart, (int)dataLen);
                        RecordedMd5 ??= Convert.ToHexString(h.AsSpan(0, 16)).ToLowerInvariant();
                        RecordedSha1 = Convert.ToHexString(h.AsSpan(16, 20)).ToLowerInvariant();
                    }
                    break;

                case "done":
                    goto DoneParsing;
            }

            if (next == pos || next == 0)
            {
                break;
            }
            pos = next;
        }

    DoneParsing:
        // Stitch every table's offsets together in order. The offset's high bit means
        // "compressed", and the offsets are relative to the table's base offset.
        foreach (var (entries, baseOff) in tableOffsets.Zip(tableBases))
        {
            foreach (var rawOff in entries)
            {
                bool compressed = (rawOff & 0x80000000L) != 0;
                long phys = (rawOff & 0x7FFFFFFFL) + baseOff;
                chunks.Add(compressed ? phys | unchecked((long)0x8000_0000_0000_0000) : phys);
            }
        }

        // For each chunk we also need the physical size: distance to the next chunk's
        // physical offset (chunks are stored back-to-back in the "sectors" section).
        // The final chunk's size is "everything to the start of the next section", which
        // we approximate by the end of the sectors section. For the common case where
        // we used a single contiguous "sectors" section, the deflate stream terminates
        // itself so reading "too much" is harmless — we let ZLibStream stop at the EOF
        // marker.
        ChunkOffsets = chunks.ToArray();
        ChunkPhysicalSizes = new long[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
        {
            long a = chunks[i] & 0x7FFFFFFFFFFFFFFFL;
            long b = (i + 1 < chunks.Count) ? chunks[i + 1] & 0x7FFFFFFFFFFFFFFFL : a + ChunkSize + 16;
            ChunkPhysicalSizes[i] = Math.Max(0, b - a);
        }

        if (BytesPerSector == 0)
        {
            throw new InvalidDataException("EWF: no volume section found.");
        }
        if (ChunkOffsets.Length == 0)
        {
            throw new InvalidDataException("EWF: no table section found.");
        }
    }

    public Stream OpenStream() => new EwfStream(this);

    internal byte[] ReadChunk(int chunkIndex)
    {
        if ((uint)chunkIndex >= (uint)ChunkOffsets.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        }
        long raw = ChunkOffsets[chunkIndex];
        bool compressed = (raw & unchecked((long)0x8000_0000_0000_0000)) != 0;
        long phys = raw & 0x7FFFFFFFFFFFFFFFL;
        int size = (int)Math.Min(ChunkPhysicalSizes[chunkIndex], int.MaxValue);

        _file.Position = phys;
        if (!compressed)
        {
            // Raw chunk + 4-byte adler-32 trailer.
            int chunkBytes = ChunkSize;
            if (chunkIndex == ChunkOffsets.Length - 1)
            {
                // Last chunk: shorter than ChunkSize if the disk size isn't a clean multiple.
                long lastSectors = (long)NumberOfSectors - (long)chunkIndex * SectorsPerChunk;
                chunkBytes = (int)Math.Min(chunkBytes, lastSectors * BytesPerSector);
            }
            var buf = new byte[chunkBytes];
            ReadExactInto(buf);
            return buf;
        }
        else
        {
            // Compressed chunk: ZLib stream (RFC 1950). Decompress up to ChunkSize bytes.
            int chunkBytes = ChunkSize;
            if (chunkIndex == ChunkOffsets.Length - 1)
            {
                long lastSectors = (long)NumberOfSectors - (long)chunkIndex * SectorsPerChunk;
                chunkBytes = (int)Math.Min(chunkBytes, lastSectors * BytesPerSector);
            }
            using var zlib = new ZLibStream(_file, CompressionMode.Decompress, leaveOpen: true);
            var buf = new byte[chunkBytes];
            int filled = 0;
            while (filled < buf.Length)
            {
                int r = zlib.Read(buf, filled, buf.Length - filled);
                if (r == 0) break;
                filled += r;
            }
            if (filled < buf.Length)
            {
                Array.Resize(ref buf, filled);
            }
            return buf;
        }
    }

    private byte[] ReadExact(int n)
    {
        var b = new byte[n];
        ReadExactInto(b);
        return b;
    }

    private void ReadExactInto(byte[] b)
    {
        int filled = 0;
        while (filled < b.Length)
        {
            int r = _file.Read(b, filled, b.Length - filled);
            if (r <= 0)
            {
                throw new EndOfStreamException();
            }
            filled += r;
        }
    }

    private byte[] ReadAt(long offset, int length)
    {
        var save = _file.Position;
        try
        {
            _file.Position = offset;
            return ReadExact(length);
        }
        finally
        {
            _file.Position = save;
        }
    }

    private static string ReadCString(ReadOnlySpan<byte> buf)
    {
        int end = buf.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? buf : buf[..end]);
    }

    private static string DecompressString(byte[] zlibBytes, bool utf16)
    {
        using var ms = new MemoryStream(zlibBytes);
        using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var bytes = raw.ToArray();
        return utf16
            ? Encoding.Unicode.GetString(bytes).TrimEnd('\0')
            : Encoding.ASCII.GetString(bytes).TrimEnd('\0');
    }

    public void Dispose()
    {
        if (_ownsStream) _file.Dispose();
    }
}
