using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Cinder.Imaging.Ewf;

/// <summary>
/// Writes Expert Witness (EnCase 6 layout) containers: <c>.E01</c>, <c>.E02</c>, … Bytes are
/// fed in through <see cref="Write(ReadOnlySpan{byte})"/>, cut into chunks, zlib-compressed
/// (or stored with an Adler-32 when compression would not help), and laid out as
/// <c>sectors</c> / <c>table</c> / <c>table2</c> runs that roll over to a new segment file
/// before the 2 GiB offset limit the 31-bit table entries impose.
///
/// <para>Segment 1 opens with two <c>header2</c> sections and a <c>header</c> (case metadata,
/// as EnCase writes them), then <c>volume</c>; every later segment opens with <c>data</c>.
/// The final segment closes with <c>digest</c>, <c>hash</c>, an <c>error2</c> section when
/// sectors were unreadable, and <c>done</c>. Media size need not be known up front: the
/// geometry sections are patched when <see cref="Finish"/> runs, so a device of unknown length
/// images correctly.</para>
///
/// <para>Every descriptor and every fixed-layout section carries the Adler-32 the format
/// requires, so libewf, FTK Imager and EnCase accept the output — not only Cinder's own
/// reader, which is more forgiving.</para>
/// </summary>
public sealed class EwfWriter : IDisposable
{
    public sealed record Options
    {
        public uint BytesPerSector { get; init; } = 512;
        public uint SectorsPerChunk { get; init; } = 64;                   // 32 KiB, the EnCase default
        public long MaxSegmentBytes { get; init; } = 1500L * 1024 * 1024;  // FTK Imager's default; must stay below 2 GiB
        public CompressionLevel Compression { get; init; } = CompressionLevel.Optimal;
        public bool Compress { get; init; } = true;
        public string? CaseNumber { get; init; }
        public string? EvidenceNumber { get; init; }
        public string? Examiner { get; init; }
        public string? Description { get; init; }
        public string? Notes { get; init; }
        public string? DeviceModel { get; init; }
        public string? DeviceSerial { get; init; }
        public string AcquisitionTool { get; init; } = "Cinder";
        /// <summary>0x00 removable, 0x01 fixed, 0x03 optical, 0x0E logical.</summary>
        public byte MediaType { get; init; } = 0x01;
        /// <summary>0x01 image file, 0x02 physical device (OR'd), 0x04 write-blocked.</summary>
        public byte MediaFlags { get; init; } = 0x02;
        public DateTimeOffset AcquisitionTime { get; init; } = DateTimeOffset.UtcNow;
    }

    private const int SegmentHeaderLength = 13;
    private const int SectionHeaderLength = 76;
    private const int MaxTableEntries = 16375;              // EnCase 6 ceiling per table section
    private const long OffsetCeiling = 0x7FFF_FFFFL;        // table entries are 31-bit
    private const int MaxSegments = 99 + 26 * 26;

    private readonly string _firstPath;
    private readonly Options _o;
    private readonly int _chunkSize;
    private readonly byte[] _pending;
    private int _pendingLength;
    private readonly List<string> _segmentPaths = [];
    private readonly List<(string Path, long Offset)> _geometrySections = [];
    private readonly List<(long FirstSector, long Count)> _badRanges = [];

    private FileStream? _seg;
    private int _segmentNumber;
    private long _sectorsDescriptorOffset = -1;
    private readonly List<uint> _tableEntries = [];
    private long _chunkCount;
    private long _bytesWritten;
    private bool _finished;

    public EwfWriter(string firstSegmentPath, Options? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstSegmentPath);
        _o = options ?? new Options();
        if (_o.BytesPerSector is 0 or > (1 << 20))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "bytes per sector must be 1..1 MiB");
        }
        if (_o.SectorsPerChunk is 0 or > (1 << 16))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "sectors per chunk must be 1..65536");
        }
        if (_o.MaxSegmentBytes < 4L * 1024 * 1024 || _o.MaxSegmentBytes > 2000L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "segment size must be between 4 MiB and 2000 MiB — table offsets are 31-bit");
        }
        _firstPath = Path.GetFullPath(firstSegmentPath);
        _chunkSize = checked((int)(_o.BytesPerSector * _o.SectorsPerChunk));
        _pending = new byte[_chunkSize];
        Directory.CreateDirectory(Path.GetDirectoryName(_firstPath)!);
        OpenSegment();
    }

    public IReadOnlyList<string> SegmentPaths => _segmentPaths;
    public long BytesWritten => _bytesWritten;
    public long ChunkCount => _chunkCount;
    public int ChunkSize => _chunkSize;

    /// <summary>Appends media bytes. Any amount; chunking is internal.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_seg is null, this);
        if (_finished)
        {
            throw new InvalidOperationException("The container has been finished.");
        }
        while (data.Length > 0)
        {
            var take = Math.Min(_chunkSize - _pendingLength, data.Length);
            data[..take].CopyTo(_pending.AsSpan(_pendingLength));
            _pendingLength += take;
            _bytesWritten += take;
            data = data[take..];
            if (_pendingLength == _chunkSize)
            {
                EmitChunk(_pending.AsSpan(0, _pendingLength));
                _pendingLength = 0;
            }
        }
    }

    /// <summary>Records a run of sectors that could not be read (they were zero-filled in the data).</summary>
    public void AddBadSectors(long firstSector, long count)
    {
        if (count <= 0)
        {
            return;
        }
        if (_badRanges.Count > 0 && _badRanges[^1].FirstSector + _badRanges[^1].Count == firstSector)
        {
            _badRanges[^1] = (_badRanges[^1].FirstSector, _badRanges[^1].Count + count);
        }
        else
        {
            _badRanges.Add((firstSector, count));
        }
    }

    /// <summary>
    /// Flushes the last chunk, closes the open table, writes the digest / hash / error / done
    /// sections and patches the geometry recorded at the start of every segment. The digests are
    /// the caller's — the writer does not hash, so the values the container records are the ones
    /// the imager computed over what it read.
    /// </summary>
    public void Finish(byte[]? md5, byte[]? sha1)
    {
        ObjectDisposedException.ThrowIf(_seg is null, this);
        if (_finished)
        {
            return;
        }
        _finished = true;

        if (_pendingLength > 0)
        {
            // The format is sector-granular: a trailing partial sector is zero-padded and the
            // padding is reported through BytesWritten staying at the true count.
            var padded = (int)(((_pendingLength + _o.BytesPerSector - 1) / _o.BytesPerSector) * _o.BytesPerSector);
            _pending.AsSpan(_pendingLength, padded - _pendingLength).Clear();
            EmitChunk(_pending.AsSpan(0, padded));
            _pendingLength = 0;
        }
        CloseSectorsRun();

        // Trailing sections must not straddle the segment limit either.
        if (_seg!.Position + 4096 > _o.MaxSegmentBytes)
        {
            RollSegment();
        }

        var digest = new byte[80];
        (md5 ?? new byte[16]).AsSpan(0, 16).CopyTo(digest);
        (sha1 ?? new byte[20]).AsSpan(0, 20).CopyTo(digest.AsSpan(16));
        WriteChecksum(digest, 76);
        WriteSection("digest", digest);

        var hash = new byte[36];
        (md5 ?? new byte[16]).AsSpan(0, 16).CopyTo(hash);
        WriteChecksum(hash, 32);
        WriteSection("hash", hash);

        if (_badRanges.Count > 0)
        {
            WriteSection("error2", BuildErrorSection());
        }

        WriteTerminator("done");
        _seg.Flush();
        _seg.Dispose();
        _seg = null;

        PatchGeometry();
    }

    public void Dispose()
    {
        _seg?.Dispose();
        _seg = null;
    }

    // ---- chunks ------------------------------------------------------------------------

    private void EmitChunk(ReadOnlySpan<byte> plain)
    {
        byte[] payload;
        bool compressed;
        if (_o.Compress && _o.Compression != CompressionLevel.NoCompression)
        {
            payload = Deflate(plain, _o.Compression);
            compressed = payload.Length < plain.Length + 4;
            if (!compressed)
            {
                payload = Stored(plain);
            }
        }
        else
        {
            payload = Stored(plain);
            compressed = false;
        }

        // Roll over when this chunk would push the segment past its limit, or the table is full.
        if (_sectorsDescriptorOffset >= 0 &&
            (_seg!.Position + payload.Length + 4096 > _o.MaxSegmentBytes || _tableEntries.Count >= MaxTableEntries))
        {
            CloseSectorsRun();
            if (_seg!.Position + payload.Length + 4096 > _o.MaxSegmentBytes)
            {
                RollSegment();
            }
        }
        if (_sectorsDescriptorOffset < 0)
        {
            OpenSectorsRun();
        }

        var offset = _seg!.Position;
        if (offset > OffsetCeiling)
        {
            throw new InvalidOperationException("EWF: chunk offset exceeds the 31-bit table limit; lower the segment size.");
        }
        _seg.Write(payload);
        _tableEntries.Add((uint)offset | (compressed ? 0x8000_0000u : 0u));
        _chunkCount++;
    }

    private static byte[] Deflate(ReadOnlySpan<byte> input, CompressionLevel level)
    {
        using var ms = new MemoryStream(input.Length / 2 + 64);
        using (var z = new ZLibStream(ms, level, leaveOpen: true))
        {
            z.Write(input);
        }
        return ms.ToArray();
    }

    /// <summary>An uncompressed chunk is the data followed by its Adler-32.</summary>
    private static byte[] Stored(ReadOnlySpan<byte> plain)
    {
        var buf = new byte[plain.Length + 4];
        plain.CopyTo(buf);
        BitConverter.TryWriteBytes(buf.AsSpan(plain.Length), Adler32(plain));
        return buf;
    }

    // ---- sections ------------------------------------------------------------------------

    private void OpenSectorsRun()
    {
        _sectorsDescriptorOffset = _seg!.Position;
        // Placeholder descriptor; size and next are patched when the run closes.
        _seg.Write(new byte[SectionHeaderLength]);
        _tableEntries.Clear();
    }

    private void CloseSectorsRun()
    {
        if (_sectorsDescriptorOffset < 0)
        {
            return;
        }
        var end = _seg!.Position;
        var size = end - _sectorsDescriptorOffset;
        _seg.Position = _sectorsDescriptorOffset;
        _seg.Write(Descriptor("sectors", next: end, size: size));
        _seg.Position = end;
        _sectorsDescriptorOffset = -1;

        var table = BuildTable();
        WriteSection("table", table);
        WriteSection("table2", table);
        _tableEntries.Clear();
    }

    private byte[] BuildTable()
    {
        // header: entry count (4), padding (4), base offset (8), padding (4), checksum (4)
        var n = _tableEntries.Count;
        var t = new byte[24 + n * 4 + 4];
        BitConverter.TryWriteBytes(t.AsSpan(0), (uint)n);
        BitConverter.TryWriteBytes(t.AsSpan(8), 0UL);
        WriteChecksum(t, 20);
        for (int i = 0; i < n; i++)
        {
            BitConverter.TryWriteBytes(t.AsSpan(24 + i * 4), _tableEntries[i]);
        }
        BitConverter.TryWriteBytes(t.AsSpan(24 + n * 4), Adler32(t.AsSpan(24, n * 4)));
        return t;
    }

    private byte[] BuildErrorSection()
    {
        // header: entry count (4), unknown (512), checksum (4); entries: first sector (4), count (4); checksum (4)
        var n = _badRanges.Count;
        var e = new byte[520 + n * 8 + 4];
        BitConverter.TryWriteBytes(e.AsSpan(0), (uint)n);
        WriteChecksum(e, 516);
        for (int i = 0; i < n; i++)
        {
            BitConverter.TryWriteBytes(e.AsSpan(520 + i * 8), (uint)Math.Min(uint.MaxValue, _badRanges[i].FirstSector));
            BitConverter.TryWriteBytes(e.AsSpan(524 + i * 8), (uint)Math.Min(uint.MaxValue, _badRanges[i].Count));
        }
        BitConverter.TryWriteBytes(e.AsSpan(520 + n * 8), Adler32(e.AsSpan(520, n * 8)));
        return e;
    }

    private byte[] BuildGeometry()
    {
        var sectorCount = (ulong)((_bytesWritten + _o.BytesPerSector - 1) / _o.BytesPerSector);
        var v = new byte[1052];
        v[0] = _o.MediaType;
        BitConverter.TryWriteBytes(v.AsSpan(4), (uint)Math.Min(uint.MaxValue, (ulong)_chunkCount));
        BitConverter.TryWriteBytes(v.AsSpan(8), _o.SectorsPerChunk);
        BitConverter.TryWriteBytes(v.AsSpan(12), _o.BytesPerSector);
        BitConverter.TryWriteBytes(v.AsSpan(16), sectorCount);
        // CHS geometry is informational; EnCase fills a plausible 255/63 decomposition.
        var cylinders = sectorCount / (255UL * 63UL);
        BitConverter.TryWriteBytes(v.AsSpan(24), (uint)Math.Min(uint.MaxValue, cylinders));
        BitConverter.TryWriteBytes(v.AsSpan(28), 255u);
        BitConverter.TryWriteBytes(v.AsSpan(32), 63u);
        v[36] = _o.MediaFlags;
        v[52] = _o.Compress && _o.Compression != CompressionLevel.NoCompression
            ? (byte)(_o.Compression == CompressionLevel.SmallestSize ? 2 : 1)
            : (byte)0;
        BitConverter.TryWriteBytes(v.AsSpan(56), 1u);   // error granularity: sectors
        _setIdentifier.CopyTo(v.AsSpan(64));
        WriteChecksum(v, 1048);
        return v;
    }

    private readonly byte[] _setIdentifier = Guid.NewGuid().ToByteArray();

    private void WriteSection(string type, ReadOnlySpan<byte> data)
    {
        var start = _seg!.Position;
        var size = SectionHeaderLength + data.Length;
        _seg.Write(Descriptor(type, next: start + size, size: size));
        _seg.Write(data);
    }

    private void WriteTerminator(string type)
    {
        var start = _seg!.Position;
        _seg.Write(Descriptor(type, next: start, size: SectionHeaderLength));
    }

    private static byte[] Descriptor(string type, long next, long size)
    {
        var d = new byte[SectionHeaderLength];
        Encoding.ASCII.GetBytes(type).CopyTo(d, 0);
        BitConverter.TryWriteBytes(d.AsSpan(16), next);
        BitConverter.TryWriteBytes(d.AsSpan(24), size);
        WriteChecksum(d, 72);
        return d;
    }

    // ---- segments ------------------------------------------------------------------------

    private void OpenSegment()
    {
        _segmentNumber++;
        if (_segmentNumber > MaxSegments)
        {
            throw new InvalidOperationException("EWF: more than 775 segments; raise the segment size.");
        }
        var path = SegmentPath(_firstPath, _segmentNumber);
        _seg = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        _segmentPaths.Add(path);

        var header = new byte[SegmentHeaderLength];
        new byte[] { 0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00 }.CopyTo(header, 0);
        header[8] = 0x01;
        BitConverter.TryWriteBytes(header.AsSpan(9), (ushort)_segmentNumber);
        _seg.Write(header);

        if (_segmentNumber == 1)
        {
            var h2 = Deflate(Header2(), CompressionLevel.Optimal);
            WriteSection("header2", h2);
            WriteSection("header2", h2);
            WriteSection("header", Deflate(Header(), CompressionLevel.Optimal));
            _geometrySections.Add((path, _seg.Position));
            WriteSection("volume", BuildGeometry());
        }
        else
        {
            _geometrySections.Add((path, _seg.Position));
            WriteSection("data", BuildGeometry());
        }
    }

    private void RollSegment()
    {
        CloseSectorsRun();
        WriteTerminator("next");
        _seg!.Flush();
        _seg.Dispose();
        _seg = null;
        OpenSegment();
    }

    /// <summary>Re-writes every volume / data section with the final chunk and sector counts.</summary>
    private void PatchGeometry()
    {
        var geometry = BuildGeometry();
        foreach (var (path, offset) in _geometrySections)
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            f.Position = offset + SectionHeaderLength;
            f.Write(geometry);
        }
    }

    /// <summary><c>.E01 … .E99</c>, then <c>.EAA … .EZZ</c> — the order the reader discovers.</summary>
    public static string SegmentPath(string firstSegmentPath, int number)
    {
        var ext = Path.GetExtension(firstSegmentPath);
        var letter = ext.Length >= 2 ? ext[1] : 'E';
        var stem = Path.Combine(Path.GetDirectoryName(firstSegmentPath) ?? ".", Path.GetFileNameWithoutExtension(firstSegmentPath));
        if (number == 1)
        {
            return firstSegmentPath;
        }
        if (number <= 99)
        {
            return stem + "." + letter + number.ToString("D2", CultureInfo.InvariantCulture);
        }
        var idx = number - 100;
        return stem + "." + letter + (char)('A' + idx / 26) + (char)('A' + idx % 26);
    }

    // ---- metadata ------------------------------------------------------------------------

    private byte[] Header2()
    {
        // EnCase 6 header2: UTF-16LE with BOM. Tabs and newlines are structural, so they are
        // stripped from every value.
        var unix = _o.AcquisitionTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("3\nmain\na\tc\tn\te\tt\tmd\tsn\tav\tov\tm\tu\tp\tdc\n");
        sb.Append(string.Join('\t',
            Clean(_o.Description), Clean(_o.CaseNumber), Clean(_o.EvidenceNumber), Clean(_o.Examiner), Clean(_o.Notes),
            Clean(_o.DeviceModel), Clean(_o.DeviceSerial), Clean(_o.AcquisitionTool), Clean(OsDescription()),
            unix, unix, "0", ""));
        sb.Append("\n\n");
        sb.Append("srce\n0\t1\np\tn\tid\tev\ttb\tlo\tpo\tah\tgu\taq\n0\t0\n\t\t\t\t\t-1\t-1\t\t\t\n\n");
        sb.Append("sub\n0\t1\np\tn\tid\tnu\tco\tgu\n0\t0\n\t\t\t\t1\t\n\n");
        var text = Encoding.Unicode.GetBytes(sb.ToString());
        var withBom = new byte[text.Length + 2];
        withBom[0] = 0xFF;
        withBom[1] = 0xFE;
        text.CopyTo(withBom, 2);
        return withBom;
    }

    private byte[] Header()
    {
        var t = _o.AcquisitionTime.ToUniversalTime();
        var stamp = string.Join(' ', t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second);
        var sb = new StringBuilder();
        sb.Append("1\nmain\nc\tn\ta\te\tt\tav\tov\tm\tu\tp\n");
        sb.Append(string.Join('\t',
            Clean(_o.CaseNumber), Clean(_o.EvidenceNumber), Clean(_o.Description), Clean(_o.Examiner), Clean(_o.Notes),
            Clean(_o.AcquisitionTool), Clean(OsDescription()), stamp, stamp, "0"));
        sb.Append("\n\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Clean(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static string OsDescription()
    {
        try
        {
            var d = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            return Encoding.ASCII.GetString(Encoding.ASCII.GetBytes(d));   // header is ASCII
        }
        catch { return "unknown"; }
    }

    // ---- checksums ------------------------------------------------------------------------

    private static void WriteChecksum(byte[] buffer, int at) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(at), Adler32(buffer.AsSpan(0, at)));

    /// <summary>Adler-32 as zlib defines it; the checksum EWF uses everywhere.</summary>
    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        while (data.Length > 0)
        {
            var n = Math.Min(data.Length, 5552);
            for (int i = 0; i < n; i++)
            {
                a += data[i];
                b += a;
            }
            a %= Mod;
            b %= Mod;
            data = data[n..];
        }
        return (b << 16) | a;
    }
}
