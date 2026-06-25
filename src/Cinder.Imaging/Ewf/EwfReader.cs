using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Cinder.Imaging.Ewf;

/// <summary>
/// In-process reader for the EnCase / FTK EWF (Expert Witness Format) container.
///
/// Handles both single-segment .E01 captures and multi-segment chains
/// (.E01 + .E02 + .E03 + … + .E99 + .EAA + .EAB + … up to .EZZ — the EnCase
/// segment-naming convention). Each segment parses its own EVF magic + section
/// chain (header2 / volume / table / sectors / done); chunk-offset tables from
/// every segment are concatenated, with per-chunk segment ownership tracked so
/// reads route to the correct backing file. Per-chunk on-demand decompression
/// uses ZLib (RFC 1950) via <see cref="ZLibStream"/>.
///
/// Reference: ASR Data's "Expert Witness Compression Format Specification v0.1.5"
/// and the libewf source.
/// </summary>
public sealed class EwfReader : IDisposable
{
    // E V F  \t  \r  \n  0xFF 0x00 — the eight-byte EVF magic prefix.
    public static ReadOnlySpan<byte> Magic => new byte[] { 0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00 };

    private readonly List<Stream> _segments;
    private readonly bool _ownsStreams;

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
    public int SegmentCount => _segments.Count;

    /// <summary>Per-chunk segment index (0-based) into <see cref="_segments"/>.</summary>
    internal int[] ChunkSegment { get; private set; } = [];
    /// <summary>Per-chunk physical offset (within its segment). Bit 63 set ⇒ compressed.</summary>
    internal long[] ChunkOffsets { get; private set; } = [];
    /// <summary>Per-chunk physical size in bytes (distance to next chunk's start in same segment).</summary>
    internal long[] ChunkPhysicalSizes { get; private set; } = [];

    /// <summary>
    /// Single-stream constructor (used by tests / smoke tools). Treats the stream as the
    /// only segment in the chain.
    /// </summary>
    public EwfReader(Stream stream, bool ownsStream = false)
        : this(new List<Stream> { stream }, ownsStream)
    {
    }

    private EwfReader(List<Stream> segments, bool ownsStreams)
    {
        foreach (var s in segments)
        {
            if (!s.CanSeek)
            {
                throw new ArgumentException("EWF reader requires seekable streams for every segment.");
            }
        }
        _segments = segments;
        _ownsStreams = ownsStreams;
        ParseAllSegments();
    }

    /// <summary>
    /// Opens an EWF chain starting at the given .E01. Subsequent segments
    /// (.E02, .E03, …, .E99, .EAA, …) are discovered by walking siblings in
    /// the same directory.
    /// </summary>
    public static EwfReader Open(string firstSegmentPath)
    {
        var paths = DiscoverSegments(firstSegmentPath);
        var streams = new List<Stream>(paths.Count);
        try
        {
            foreach (var p in paths)
            {
                streams.Add(new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
        }
        catch
        {
            foreach (var s in streams) { try { s.Dispose(); } catch { } }
            throw;
        }
        return new EwfReader(streams, ownsStreams: true);
    }

    /// <summary>
    /// Walks the directory containing <paramref name="firstSegmentPath"/> and gathers
    /// every sibling segment file in EnCase naming order. Returns at minimum the first
    /// segment; missing siblings stop the walk (we don't go ahead and try to open a gap).
    /// </summary>
    public static IReadOnlyList<string> DiscoverSegments(string firstSegmentPath)
    {
        var first = Path.GetFullPath(firstSegmentPath);
        var dir = Path.GetDirectoryName(first) ?? ".";
        var ext = Path.GetExtension(first);
        if (ext.Length < 4)
        {
            return new[] { first };
        }
        var stem = Path.GetFileNameWithoutExtension(first);
        var letter = ext[1];           // 'E' for .E01, 'L' for .L01, etc.
        var found = new List<string> { first };
        for (int n = 2; n <= 99; n++)
        {
            var candidate = Path.Combine(dir, stem + "." + letter + n.ToString("D2", CultureInfo.InvariantCulture));
            if (File.Exists(candidate))
            {
                found.Add(candidate);
            }
            else
            {
                if (n >= 100) break;
                // For 2..99 a missing segment in the middle is fatal — chain has a hole.
                if (found.Count > 1) break;
                else break;
            }
        }
        // Letter-suffix segments .EAA .. .EZZ for chains > 99 segments.
        for (char a = 'A'; a <= 'Z'; a++)
        {
            for (char b = 'A'; b <= 'Z'; b++)
            {
                var candidate = Path.Combine(dir, stem + "." + letter + a + b);
                if (File.Exists(candidate))
                {
                    found.Add(candidate);
                }
                else
                {
                    goto Done;
                }
            }
        }
    Done:
        return found;
    }

    private void ParseAllSegments()
    {
        var globalChunkSeg = new List<int>();
        var globalChunkOff = new List<long>();

        for (int segIx = 0; segIx < _segments.Count; segIx++)
        {
            ParseSegment(segIx, globalChunkSeg, globalChunkOff);
        }

        if (BytesPerSector == 0)
        {
            throw new InvalidDataException("EWF: no volume section found in any segment.");
        }
        if (globalChunkOff.Count == 0)
        {
            throw new InvalidDataException("EWF: no table section found in any segment.");
        }

        ChunkSegment = globalChunkSeg.ToArray();
        ChunkOffsets = globalChunkOff.ToArray();

        // For each chunk: physical size = distance to the next chunk's physical offset
        // ONLY IF the next chunk is in the same segment. Otherwise fall back to ChunkSize
        // (a deflate stream will terminate itself at the chunk boundary).
        ChunkPhysicalSizes = new long[ChunkOffsets.Length];
        for (int i = 0; i < ChunkOffsets.Length; i++)
        {
            long a = ChunkOffsets[i] & 0x7FFFFFFFFFFFFFFFL;
            long b;
            if (i + 1 < ChunkOffsets.Length && ChunkSegment[i + 1] == ChunkSegment[i])
            {
                b = ChunkOffsets[i + 1] & 0x7FFFFFFFFFFFFFFFL;
            }
            else
            {
                b = a + ChunkSize + 16;
            }
            ChunkPhysicalSizes[i] = Math.Max(0, b - a);
        }
    }

    private void ParseSegment(int segIx, List<int> globalChunkSeg, List<long> globalChunkOff)
    {
        var file = _segments[segIx];

        // Magic + segment header (8 magic + 1 fields-start + 2 segment-num + 1 fields-end + 1 unused = 13).
        file.Position = 0;
        var magic = ReadExact(file, 13);
        if (!magic.AsSpan(0, 8).SequenceEqual(Magic))
        {
            throw new InvalidDataException(
                $"EWF: bad magic in segment #{segIx} (expected EVF prefix).");
        }

        long pos = 13;
        var segTables = new List<List<long>>();
        var segTableBases = new List<long>();

        while (true)
        {
            file.Position = pos;
            var hdr = ReadExact(file, 76);
            var type = ReadCString(hdr.AsSpan(0, 16));
            var next = BitConverter.ToInt64(hdr, 16);
            var size = BitConverter.ToInt64(hdr, 24);

            var dataStart = pos + 76;
            var dataLen = checked(size - 76);

            switch (type)
            {
                case "header2":
                case "header":
                    if (CaseDescription is null && dataLen > 0)
                    {
                        var raw = ReadAt(file, dataStart, (int)dataLen);
                        try { CaseDescription = DecompressString(raw, type == "header2"); }
                        catch { /* tolerate broken header */ }
                    }
                    break;

                case "disk":
                case "volume":
                    if (BytesPerSector == 0 && dataLen >= 32)
                    {
                        var v = ReadAt(file, dataStart, (int)dataLen);
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
                        var t = ReadAt(file, dataStart, (int)dataLen);
                        var entryCount = BitConverter.ToUInt32(t, 0);
                        var tableBase = BitConverter.ToUInt64(t, 8);
                        var entries = new List<long>(checked((int)entryCount));
                        for (long i = 0; i < entryCount; i++)
                        {
                            var off = BitConverter.ToUInt32(t, 24 + (int)(i * 4));
                            entries.Add(off);
                        }
                        segTables.Add(entries);
                        segTableBases.Add((long)tableBase);
                    }
                    break;

                case "hash":
                    if (dataLen >= 20)
                    {
                        var h = ReadAt(file, dataStart, (int)dataLen);
                        RecordedMd5 = Convert.ToHexString(h.AsSpan(0, 16)).ToLowerInvariant();
                    }
                    break;

                case "digest":
                    if (dataLen >= 36)
                    {
                        var h = ReadAt(file, dataStart, (int)dataLen);
                        RecordedMd5 ??= Convert.ToHexString(h.AsSpan(0, 16)).ToLowerInvariant();
                        RecordedSha1 = Convert.ToHexString(h.AsSpan(16, 20)).ToLowerInvariant();
                    }
                    break;

                case "done":
                    goto SegmentDone;
            }

            if (next == pos || next == 0)
            {
                break;
            }
            pos = next;
        }

    SegmentDone:
        for (int t = 0; t < segTables.Count; t++)
        {
            var entries = segTables[t];
            var baseOff = segTableBases[t];
            foreach (var rawOff in entries)
            {
                bool compressed = (rawOff & 0x80000000L) != 0;
                long phys = (rawOff & 0x7FFFFFFFL) + baseOff;
                long encoded = compressed
                    ? phys | unchecked((long)0x8000_0000_0000_0000)
                    : phys;
                globalChunkSeg.Add(segIx);
                globalChunkOff.Add(encoded);
            }
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
        int segIx = ChunkSegment[chunkIndex];
        var file = _segments[segIx];

        file.Position = phys;
        int chunkBytes = ChunkSize;
        if (chunkIndex == ChunkOffsets.Length - 1)
        {
            long lastSectors = (long)NumberOfSectors - (long)chunkIndex * SectorsPerChunk;
            chunkBytes = (int)Math.Min(chunkBytes, lastSectors * BytesPerSector);
        }

        if (!compressed)
        {
            var buf = new byte[chunkBytes];
            ReadExactInto(file, buf);
            return buf;
        }
        else
        {
            using var zlib = new ZLibStream(file, CompressionMode.Decompress, leaveOpen: true);
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

    private static byte[] ReadExact(Stream s, int n)
    {
        var b = new byte[n];
        ReadExactInto(s, b);
        return b;
    }

    private static void ReadExactInto(Stream s, byte[] b)
    {
        int filled = 0;
        while (filled < b.Length)
        {
            int r = s.Read(b, filled, b.Length - filled);
            if (r <= 0)
            {
                throw new EndOfStreamException();
            }
            filled += r;
        }
    }

    private static byte[] ReadAt(Stream s, long offset, int length)
    {
        var save = s.Position;
        try
        {
            s.Position = offset;
            return ReadExact(s, length);
        }
        finally
        {
            s.Position = save;
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
        if (_ownsStreams)
        {
            foreach (var s in _segments) { try { s.Dispose(); } catch { } }
        }
    }
}
