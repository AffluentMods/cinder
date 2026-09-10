using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
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
/// <para><b>Hostile input.</b> Every length, count and offset in an EWF container is
/// attacker-controlled — the whole point of the tool is to open images of unknown
/// provenance. This parser therefore treats each of them as untrusted: section sizes are
/// bounded and checked against the file, the section chain is required to make forward
/// progress (so a cycle can't hang the parser), table entry counts must fit the section
/// that declares them, geometry is sanity-checked, and every decompression is bounded.
/// Malformed input yields <see cref="InvalidDataException"/>, never a hang or an OOM.</para>
///
/// <para><b>Damaged chunks.</b> A chunk that fails to decompress is zero-filled to its
/// declared length and recorded in <see cref="DamagedChunks"/> rather than shortening the
/// stream. Silently returning fewer bytes would present a truncated image to the hasher
/// and the carver as if it were complete — see <see cref="EwfStream"/>.</para>
///
/// Reference: ASR Data's "Expert Witness Compression Format Specification v0.1.5"
/// and the libewf source.
/// </summary>
public sealed class EwfReader : IDisposable
{
    // E V F  \t  \r  \n  0xFF 0x00 — the eight-byte EVF magic prefix.
    public static ReadOnlySpan<byte> Magic => new byte[] { 0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00 };

    /// <summary>Bytes in a segment's file header: magic(8) + fields-start(1) + segment-number(2) + fields-end(2).</summary>
    private const int SegmentHeaderLength = 13;

    /// <summary>Bytes in a section descriptor: type(16) + next(8) + size(8) + padding(40) + checksum(4).</summary>
    private const int SectionHeaderLength = 76;

    /// <summary>
    /// Ceiling on a single section's data payload. Real header/volume/hash sections are a few
    /// hundred bytes; the largest legitimate table section is roughly 64K entries × 4 bytes.
    /// 64 MiB is far above anything a real acquisition produces and refuses a container that
    /// declares a section larger than memory.
    /// </summary>
    private const long MaxSectionDataBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Ceiling on chunks across the whole chain. Each chunk costs 12 bytes of index, so this
    /// bounds the index at ~800 MB. At the usual 32 KiB chunk size it admits a ~2 TB image;
    /// larger images need the streaming index tracked in ROADMAP Phase 2.
    /// </summary>
    private const int MaxTotalChunks = 1 << 26;

    /// <summary>Ceiling on the decompressed size of a header string. Bounds a zlib bomb.</summary>
    private const int MaxHeaderStringBytes = 8 * 1024 * 1024;

    /// <summary>Ceiling on section-chain hops per segment. Backstop against a pathological chain.</summary>
    private const int MaxSectionsPerSegment = 1 << 16;

    /// <summary>Largest chunk geometry we will accept (sectors-per-chunk × bytes-per-sector).</summary>
    private const int MaxChunkSizeBytes = 64 * 1024 * 1024;

    private readonly List<Stream> _segments;
    private readonly bool _ownsStreams;
    private readonly List<int> _damagedChunks = [];

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

    /// <summary>
    /// Chunks that failed to decompress and were zero-filled, in the order they were first
    /// read. Populated lazily by <see cref="ReadChunk"/>; empty until the media is read.
    /// A non-empty list means the image is damaged and any hash over it will not match the
    /// acquisition hash.
    /// </summary>
    public IReadOnlyList<int> DamagedChunks => _damagedChunks;

    /// <summary>Per-chunk segment index (0-based) into <see cref="_segments"/>.</summary>
    internal int[] ChunkSegment { get; private set; } = [];

    /// <summary>Per-chunk physical offset (within its segment). Bit 63 set ⇒ compressed.</summary>
    internal long[] ChunkOffsets { get; private set; } = [];

    /// <summary>
    /// Chunks actually present in the concatenated chunk tables. This is the authority for
    /// what can be read; <see cref="NumberOfChunks"/> is only what the volume section claims,
    /// and a malformed container can disagree.
    /// </summary>
    internal int ChunkCount => ChunkOffsets.Length;

    /// <summary>
    /// Single-stream constructor (used by tests / smoke tools). Treats the stream as the
    /// only segment in the chain.
    /// </summary>
    public EwfReader(Stream stream, bool ownsStream = false)
        : this([stream], ownsStream)
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

        try
        {
            return new EwfReader(streams, ownsStreams: true);
        }
        catch
        {
            foreach (var s in streams) { try { s.Dispose(); } catch { } }
            throw;
        }
    }

    /// <summary>
    /// Walks the directory containing <paramref name="firstSegmentPath"/> and gathers every
    /// sibling segment in EnCase naming order: <c>.E02 … .E99</c>, then <c>.EAA … .EZZ</c>
    /// for chains longer than 99 segments. The walk stops at the first gap — a chain with a
    /// hole is truncated rather than silently stitched across the missing segment, so the
    /// caller sees a short media size instead of misaligned data.
    /// </summary>
    public static IReadOnlyList<string> DiscoverSegments(string firstSegmentPath)
    {
        var first = Path.GetFullPath(firstSegmentPath);
        var dir = Path.GetDirectoryName(first) ?? ".";
        var ext = Path.GetExtension(first);
        if (ext.Length < 4)
        {
            return [first];
        }

        var stem = Path.GetFileNameWithoutExtension(first);
        var letter = ext[1];           // 'E' for .E01, 'L' for .L01, etc.
        var found = new List<string> { first };

        for (int n = 2; n <= 99; n++)
        {
            var candidate = Path.Combine(dir, stem + "." + letter + n.ToString("D2", CultureInfo.InvariantCulture));
            if (!File.Exists(candidate))
            {
                return found;
            }
            found.Add(candidate);
        }

        // Letter-suffix segments .xAA … .xZZ continue a chain that filled all 99 numeric
        // slots. Only reachable when the numeric run completed, so a stray .EAA next to a
        // two-segment chain is not appended.
        for (char a = 'A'; a <= 'Z'; a++)
        {
            for (char b = 'A'; b <= 'Z'; b++)
            {
                var candidate = Path.Combine(dir, stem + "." + letter + a + b);
                if (!File.Exists(candidate))
                {
                    return found;
                }
                found.Add(candidate);
            }
        }

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
            throw new InvalidDataException("EWF: no usable volume section found in any segment.");
        }
        if (globalChunkOff.Count == 0)
        {
            throw new InvalidDataException("EWF: no table section found in any segment.");
        }

        ChunkSegment = [.. globalChunkSeg];
        ChunkOffsets = [.. globalChunkOff];
    }

    private void ParseSegment(int segIx, List<int> globalChunkSeg, List<long> globalChunkOff)
    {
        var file = _segments[segIx];
        var fileLength = file.Length;

        file.Position = 0;
        var header = ReadExact(file, SegmentHeaderLength);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic))
        {
            throw new InvalidDataException(
                $"EWF: bad magic in segment #{segIx} (expected EVF prefix).");
        }

        long pos = SegmentHeaderLength;
        var segTables = new List<List<long>>();
        var segTableBases = new List<long>();

        // The chain is required to move strictly forward. Without that a container whose
        // sections point at each other (or at themselves) would loop here forever.
        var hops = 0;

        while (true)
        {
            if (++hops > MaxSectionsPerSegment)
            {
                throw new InvalidDataException(
                    $"EWF: segment #{segIx} exceeds {MaxSectionsPerSegment} sections — refusing to continue.");
            }
            if (pos < 0 || pos + SectionHeaderLength > fileLength)
            {
                throw new InvalidDataException(
                    $"EWF: segment #{segIx} section descriptor at offset {pos} lies outside the file.");
            }

            file.Position = pos;
            var hdr = ReadExact(file, SectionHeaderLength);
            var type = ReadCString(hdr.AsSpan(0, 16));
            var next = BitConverter.ToInt64(hdr, 16);
            var size = BitConverter.ToInt64(hdr, 24);

            var dataStart = pos + SectionHeaderLength;
            var dataLen = ValidateSectionData(segIx, type, pos, size, dataStart, fileLength);

            switch (type)
            {
                case "header2":
                case "header":
                    if (CaseDescription is null && dataLen > 0)
                    {
                        var raw = ReadAt(file, dataStart, (int)dataLen);
                        try { CaseDescription = DecompressString(raw, type == "header2"); }
                        catch { /* tolerate a broken header — it is metadata, not evidence */ }
                    }
                    break;

                case "disk":
                case "volume":
                    if (BytesPerSector == 0 && dataLen >= 32)
                    {
                        ParseVolume(ReadAt(file, dataStart, (int)dataLen), segIx);
                    }
                    break;

                case "table":
                    if (dataLen >= 24)
                    {
                        ParseTable(ReadAt(file, dataStart, (int)dataLen), segIx, segTables, segTableBases);
                    }
                    break;

                case "hash":
                    if (dataLen >= 16)
                    {
                        var h = ReadAt(file, dataStart, (int)dataLen);
                        RecordedMd5 ??= Convert.ToHexStringLower(h.AsSpan(0, 16));
                    }
                    break;

                case "digest":
                    if (dataLen >= 36)
                    {
                        var h = ReadAt(file, dataStart, (int)dataLen);
                        RecordedMd5 ??= Convert.ToHexStringLower(h.AsSpan(0, 16));
                        RecordedSha1 ??= Convert.ToHexStringLower(h.AsSpan(16, 20));
                    }
                    break;

                case "done":
                    goto SegmentDone;
            }

            // A self-referential or zero `next` marks the end of the chain. Anything that
            // doesn't advance is a malformed chain, not a terminator.
            if (next == 0 || next == pos)
            {
                break;
            }
            if (next < pos)
            {
                throw new InvalidDataException(
                    $"EWF: segment #{segIx} section chain moves backwards ({pos} → {next}).");
            }
            pos = next;
        }

    SegmentDone:
        for (int t = 0; t < segTables.Count; t++)
        {
            var entries = segTables[t];
            var baseOff = segTableBases[t];

            if (globalChunkOff.Count + entries.Count > MaxTotalChunks)
            {
                throw new InvalidDataException(
                    $"EWF: chunk table exceeds the {MaxTotalChunks:N0}-chunk ceiling.");
            }

            foreach (var rawOff in entries)
            {
                bool compressed = (rawOff & 0x80000000L) != 0;
                long phys = (rawOff & 0x7FFFFFFFL) + baseOff;

                // Offsets are used verbatim as stream positions later; reject the ones that
                // could not possibly address data in this segment.
                if (phys < 0 || phys >= fileLength)
                {
                    throw new InvalidDataException(
                        $"EWF: segment #{segIx} chunk offset {phys} lies outside the file ({fileLength} bytes).");
                }

                long encoded = compressed
                    ? phys | unchecked((long)0x8000_0000_0000_0000)
                    : phys;
                globalChunkSeg.Add(segIx);
                globalChunkOff.Add(encoded);
            }
        }
    }

    /// <summary>
    /// Turns a section's declared <paramref name="size"/> into a payload length that is safe to
    /// allocate and read, or throws. Guards against negative sizes, int truncation, sizes that
    /// run past the end of the file, and sizes large enough to exhaust memory.
    /// </summary>
    private static long ValidateSectionData(int segIx, string type, long pos, long size, long dataStart, long fileLength)
    {
        if (size < SectionHeaderLength)
        {
            throw new InvalidDataException(
                $"EWF: segment #{segIx} section '{type}' at {pos} declares size {size}, below the {SectionHeaderLength}-byte header.");
        }

        var dataLen = size - SectionHeaderLength;
        if (dataLen > MaxSectionDataBytes)
        {
            throw new InvalidDataException(
                $"EWF: segment #{segIx} section '{type}' declares {dataLen:N0} bytes, above the {MaxSectionDataBytes:N0}-byte ceiling.");
        }
        if (dataStart + dataLen > fileLength)
        {
            throw new InvalidDataException(
                $"EWF: segment #{segIx} section '{type}' runs past the end of the file.");
        }
        return dataLen;
    }

    private void ParseVolume(byte[] v, int segIx)
    {
        var chunkCount = BitConverter.ToUInt32(v, 4);
        var sectorsPerChunk = BitConverter.ToUInt32(v, 8);
        var bytesPerSector = BitConverter.ToUInt32(v, 12);
        // EnCase 5 and later store the sector count as 64 bits; earlier writers left the upper
        // half zero, so reading 64 is right for both and lets a > 2 TiB image size correctly.
        var sectorCount = BitConverter.ToUInt64(v, 16);

        // Geometry drives every subsequent allocation and offset computation, so it has to be
        // plausible before we adopt it. A zero or absurd value here would otherwise surface as
        // a divide-by-zero, an overflow, or a multi-gigabyte chunk buffer.
        if (bytesPerSector is 0 or > (1 << 20))
        {
            throw new InvalidDataException(
                $"EWF: segment #{segIx} declares implausible bytes-per-sector ({bytesPerSector}).");
        }
        if (sectorsPerChunk is 0 or > (1 << 20))
        {
            throw new InvalidDataException(
                $"EWF: segment #{segIx} declares implausible sectors-per-chunk ({sectorsPerChunk}).");
        }

        var chunkBytes = (long)sectorsPerChunk * bytesPerSector;
        if (chunkBytes > MaxChunkSizeBytes)
        {
            throw new InvalidDataException(
                $"EWF: segment #{segIx} declares a {chunkBytes:N0}-byte chunk, above the {MaxChunkSizeBytes:N0}-byte ceiling.");
        }

        if (sectorCount > (ulong)(long.MaxValue / bytesPerSector))
        {
            throw new InvalidDataException($"EWF: segment #{segIx} declares an overflowing media size.");
        }

        NumberOfChunks = chunkCount;
        SectorsPerChunk = sectorsPerChunk;
        BytesPerSector = bytesPerSector;
        NumberOfSectors = sectorCount;
    }

    private static void ParseTable(byte[] t, int segIx, List<List<long>> segTables, List<long> segTableBases)
    {
        var entryCount = BitConverter.ToUInt32(t, 0);
        var tableBase = BitConverter.ToUInt64(t, 8);

        // The declared entry count must fit the section that declared it. Without this check a
        // container claiming four billion entries either allocates 32 GB or walks off the end
        // of `t` — the classic malformed-evidence crash.
        var needed = 24L + (long)entryCount * 4;
        if (needed > t.Length)
        {
            throw new InvalidDataException(
                $"EWF: segment #{segIx} table declares {entryCount:N0} entries needing {needed:N0} bytes, but the section holds {t.Length:N0}.");
        }
        if (entryCount > MaxTotalChunks)
        {
            throw new InvalidDataException(
                $"EWF: segment #{segIx} table declares {entryCount:N0} entries, above the {MaxTotalChunks:N0} ceiling.");
        }
        if (tableBase > long.MaxValue)
        {
            throw new InvalidDataException($"EWF: segment #{segIx} table declares an out-of-range base offset.");
        }

        var entries = new List<long>((int)entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            entries.Add(BitConverter.ToUInt32(t, 24 + (i * 4)));
        }
        segTables.Add(entries);
        segTableBases.Add((long)tableBase);
    }

    public Stream OpenStream() => new EwfStream(this);

    /// <summary>
    /// Decodes one chunk. Always returns exactly the chunk's logical length: a chunk that
    /// fails to decompress, or that is truncated on disk, is zero-filled and recorded in
    /// <see cref="DamagedChunks"/>. Returning a short buffer instead would make
    /// <see cref="EwfStream"/> report a premature end-of-stream, and a hash taken over that
    /// stream would cover only part of the image while looking like a complete read.
    /// </summary>
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

        var chunkBytes = LogicalChunkLength(chunkIndex);
        if (chunkBytes <= 0)
        {
            return [];
        }

        if (phys < 0 || phys >= file.Length)
        {
            return Damaged(chunkIndex, chunkBytes);
        }

        var buf = new byte[chunkBytes];
        file.Position = phys;

        if (!compressed)
        {
            // A stored chunk is raw bytes. A short read means the segment is truncated.
            var filled = ReadUpTo(file, buf);
            if (filled < buf.Length)
            {
                Array.Clear(buf, filled, buf.Length - filled);
                RecordDamage(chunkIndex);
            }
            return buf;
        }

        try
        {
            // Bound the deflate stream to this chunk's physical extent so a corrupt chunk
            // can't consume the following chunk's bytes as if they were its own.
            using var slice = new BoundedStream(file, phys, PhysicalChunkLength(chunkIndex, file.Length));
            using var zlib = new ZLibStream(slice, CompressionMode.Decompress, leaveOpen: true);

            var filled = 0;
            while (filled < buf.Length)
            {
                int r = zlib.Read(buf, filled, buf.Length - filled);
                if (r == 0)
                {
                    break;
                }
                filled += r;
            }

            if (filled < buf.Length)
            {
                Array.Clear(buf, filled, buf.Length - filled);
                RecordDamage(chunkIndex);
            }
            return buf;
        }
        catch (InvalidDataException)
        {
            return Damaged(chunkIndex, chunkBytes);
        }
        catch (EndOfStreamException)
        {
            return Damaged(chunkIndex, chunkBytes);
        }
    }

    /// <summary>Logical (decoded) length of a chunk — full chunk size except for a short final chunk.</summary>
    private int LogicalChunkLength(int chunkIndex)
    {
        var full = ChunkSize;
        if (chunkIndex != ChunkOffsets.Length - 1)
        {
            return full;
        }
        long remaining = (long)NumberOfSectors - (long)chunkIndex * SectorsPerChunk;
        if (remaining <= 0)
        {
            return 0;
        }
        return (int)Math.Min(full, remaining * BytesPerSector);
    }

    /// <summary>
    /// Physical extent of a chunk within its segment: the distance to the next chunk that
    /// lives in the same segment, or the rest of the segment for the last one.
    /// </summary>
    private long PhysicalChunkLength(int chunkIndex, long segmentLength)
    {
        long start = ChunkOffsets[chunkIndex] & 0x7FFFFFFFFFFFFFFFL;
        long endOffset = segmentLength;

        if (chunkIndex + 1 < ChunkOffsets.Length && ChunkSegment[chunkIndex + 1] == ChunkSegment[chunkIndex])
        {
            var next = ChunkOffsets[chunkIndex + 1] & 0x7FFFFFFFFFFFFFFFL;
            if (next > start)
            {
                endOffset = Math.Min(endOffset, next);
            }
        }
        return Math.Max(0, endOffset - start);
    }

    private byte[] Damaged(int chunkIndex, int length)
    {
        RecordDamage(chunkIndex);
        return new byte[length];
    }

    private void RecordDamage(int chunkIndex)
    {
        // Bounded so a wholly corrupt image can't grow this list without limit.
        if (_damagedChunks.Count < 100_000 && (_damagedChunks.Count == 0 || _damagedChunks[^1] != chunkIndex))
        {
            _damagedChunks.Add(chunkIndex);
        }
    }

    /// <summary>
    /// Re-reads the entire decoded media and compares the result against the MD5 / SHA-1 the
    /// acquisition tool recorded inside the container.
    ///
    /// <para>This is the only thing that makes <see cref="RecordedMd5"/> and
    /// <see cref="RecordedSha1"/> mean anything. Those properties are the hashes the container
    /// <em>claims</em> about itself — an examiner reading them off a metadata panel is reading
    /// the suspect file's own assertion, not a verification. Call this to turn the claim into
    /// a finding.</para>
    /// </summary>
    public async Task<EwfVerificationResult> VerifyAsync(
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        using var md5 = MD5.Create();
        using var sha1 = SHA1.Create();

        await using var stream = OpenStream();
        var buffer = new byte[4 * 1024 * 1024];
        long total = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            md5.TransformBlock(buffer, 0, read, null, 0);
            sha1.TransformBlock(buffer, 0, read, null, 0);
            total += read;
            progress?.Report(total);
        }

        md5.TransformFinalBlock([], 0, 0);
        sha1.TransformFinalBlock([], 0, 0);

        var computedMd5 = Convert.ToHexStringLower(md5.Hash!);
        var computedSha1 = Convert.ToHexStringLower(sha1.Hash!);

        var md5Match = RecordedMd5 is not null
            ? string.Equals(RecordedMd5, computedMd5, StringComparison.OrdinalIgnoreCase)
            : (bool?)null;
        var sha1Match = RecordedSha1 is not null
            ? string.Equals(RecordedSha1, computedSha1, StringComparison.OrdinalIgnoreCase)
            : (bool?)null;

        return new EwfVerificationResult(
            BytesVerified: total,
            ExpectedMd5: RecordedMd5,
            ComputedMd5: computedMd5,
            ExpectedSha1: RecordedSha1,
            ComputedSha1: computedSha1,
            Md5Match: md5Match,
            Sha1Match: sha1Match,
            DamagedChunkCount: _damagedChunks.Count);
    }

    private static byte[] ReadExact(Stream s, int n)
    {
        var b = new byte[n];
        var filled = ReadUpTo(s, b);
        if (filled < n)
        {
            throw new EndOfStreamException();
        }
        return b;
    }

    /// <summary>Reads up to <c>b.Length</c> bytes, returning how many were actually available.</summary>
    private static int ReadUpTo(Stream s, byte[] b)
    {
        int filled = 0;
        while (filled < b.Length)
        {
            int r = s.Read(b, filled, b.Length - filled);
            if (r <= 0)
            {
                break;
            }
            filled += r;
        }
        return filled;
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

    /// <summary>
    /// Inflates a header section. Output is capped at <see cref="MaxHeaderStringBytes"/>: the
    /// header is a few hundred bytes of case metadata in every real container, and an unbounded
    /// copy here would let a crafted zlib stream exhaust memory before any evidence is read.
    /// </summary>
    private static string DecompressString(byte[] zlibBytes, bool utf16)
    {
        using var ms = new MemoryStream(zlibBytes);
        using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
        using var raw = new MemoryStream();

        var buffer = new byte[64 * 1024];
        var total = 0;
        while (true)
        {
            var read = zlib.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            total += read;
            if (total > MaxHeaderStringBytes)
            {
                throw new InvalidDataException("EWF: header section decompresses beyond its permitted size.");
            }
            raw.Write(buffer, 0, read);
        }

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

    /// <summary>
    /// Read-only view of a fixed byte range of an underlying seekable stream, so a decompressor
    /// handed a corrupt chunk cannot read past that chunk's physical extent.
    /// </summary>
    private sealed class BoundedStream(Stream inner, long start, long length) : Stream
    {
        private long _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = length - _offset;
            if (remaining <= 0)
            {
                return 0;
            }
            var want = (int)Math.Min(buffer.Length, remaining);
            inner.Position = start + _offset;
            var read = inner.Read(buffer[..want]);
            _offset += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Outcome of re-hashing an EWF container against the hashes recorded inside it.</summary>
/// <param name="BytesVerified">Total decoded bytes read.</param>
/// <param name="ExpectedMd5">MD5 recorded in the container, or null if it recorded none.</param>
/// <param name="ComputedMd5">MD5 actually computed over the decoded media.</param>
/// <param name="ExpectedSha1">SHA-1 recorded in the container, or null if it recorded none.</param>
/// <param name="ComputedSha1">SHA-1 actually computed over the decoded media.</param>
/// <param name="Md5Match">True/false when the container recorded an MD5; null when it did not.</param>
/// <param name="Sha1Match">True/false when the container recorded a SHA-1; null when it did not.</param>
/// <param name="DamagedChunkCount">Chunks that had to be zero-filled during the read.</param>
public sealed record EwfVerificationResult(
    long BytesVerified,
    string? ExpectedMd5,
    string ComputedMd5,
    string? ExpectedSha1,
    string ComputedSha1,
    bool? Md5Match,
    bool? Sha1Match,
    int DamagedChunkCount)
{
    /// <summary>
    /// True only when the container recorded at least one hash and every hash it recorded
    /// matched. A container that records no hashes cannot be verified and returns false —
    /// "nothing to check" must never read as "checked and fine".
    /// </summary>
    public bool Verified =>
        DamagedChunkCount == 0 &&
        (Md5Match is not null || Sha1Match is not null) &&
        Md5Match is not false &&
        Sha1Match is not false;

    /// <summary>One-line summary suitable for a status badge or a report line.</summary>
    public string Summary()
    {
        if (Md5Match is null && Sha1Match is null)
        {
            return $"Unverifiable — container records no acquisition hash. Computed SHA-1 {ComputedSha1}.";
        }
        if (Verified)
        {
            var which = Sha1Match is not null ? "SHA-1" : "MD5";
            return $"Verified — {which} matches the acquisition hash over {BytesVerified:N0} bytes.";
        }

        var failed = new List<string>();
        if (Md5Match is false) failed.Add($"MD5 expected {ExpectedMd5}, got {ComputedMd5}");
        if (Sha1Match is false) failed.Add($"SHA-1 expected {ExpectedSha1}, got {ComputedSha1}");
        if (DamagedChunkCount > 0) failed.Add($"{DamagedChunkCount:N0} damaged chunk(s) zero-filled");
        return "VERIFICATION FAILED — " + string.Join("; ", failed);
    }
}
