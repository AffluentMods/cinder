using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Cinder.Imaging.Aff4;

/// <summary>
/// Writes AFF4 physical-image containers (AFF4 Standard v1.0 layout, the one both libaff4 and
/// pyaff4 read): a ZIP volume holding <c>container.description</c>, <c>version.txt</c>,
/// <c>information.turtle</c>, an <c>ImageStream</c> stored as bevies of zlib-compressed 32 KiB
/// chunks with a (uint64 offset, uint32 length) index per bevy, a single-range <c>Map</c> over the
/// stream, and an <c>Image</c> whose <c>aff4:dataStream</c> is the map — the object structure
/// Evimetry and pyaff4's imager produce, so the result opens as a disk image rather than a
/// bare stream.
///
/// <para>Digests are the caller's, recorded as <c>aff4:hash</c> literals with the MD5 / SHA1 /
/// SHA256 datatypes. Case metadata goes into the turtle as literals on the image. Media size
/// need not be known up front; the turtle is written last.</para>
/// </summary>
public sealed class Aff4Writer : IDisposable
{
    public sealed record Options
    {
        public bool Compress { get; init; } = true;
        public int ChunkSize { get; init; } = 32 * 1024;
        public int ChunksPerSegment { get; init; } = 2048;
        public string? CaseNumber { get; init; }
        public string? EvidenceNumber { get; init; }
        public string? Examiner { get; init; }
        public string? Description { get; init; }
        public string? Notes { get; init; }
        public string? Source { get; init; }
        public string Tool { get; init; } = "Cinder";
    }

    public const string ZlibCompression = "https://www.ietf.org/rfc/rfc1950.txt";
    public const string StoredCompression = "http://aff4.org/Schema#NullCompressor";

    private readonly Options _o;
    private readonly FileStream _file;
    private readonly ZipArchive _zip;
    private readonly byte[] _pending;
    private int _pendingLength;

    private readonly MemoryStream _bevy = new();
    private readonly List<uint> _bevyIndex = [];
    private int _bevyNumber;
    private long _size;
    private bool _finished;

    public Aff4Writer(string path, Options? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _o = options ?? new Options();
        if (_o.ChunkSize is < 512 or > (1 << 24) || _o.ChunksPerSegment is < 1 or > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        VolumeUrn = "aff4://" + Guid.NewGuid().ToString("D");
        ImageUrn = "aff4://" + Guid.NewGuid().ToString("D");
        MapUrn = "aff4://" + Guid.NewGuid().ToString("D");
        StreamUrn = "aff4://" + Guid.NewGuid().ToString("D");

        _file = new FileStream(Path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        _zip = new ZipArchive(_file, ZipArchiveMode.Create, leaveOpen: true);
        _zip.Comment = VolumeUrn;   // pyaff4 and libaff4 take the volume URN from the archive comment
        _pending = new byte[_o.ChunkSize];

        WriteMember("container.description", Encoding.UTF8.GetBytes(VolumeUrn));
        WriteMember("version.txt", Encoding.ASCII.GetBytes("major=1\nminor=0\ntool=" + Clean(_o.Tool) + "\n"));
    }

    public string Path { get; }
    public string VolumeUrn { get; }
    public string ImageUrn { get; }
    public string MapUrn { get; }
    public string StreamUrn { get; }
    public long BytesWritten => _size;

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The container has been finished.");
        }
        while (data.Length > 0)
        {
            var take = Math.Min(_o.ChunkSize - _pendingLength, data.Length);
            data[..take].CopyTo(_pending.AsSpan(_pendingLength));
            _pendingLength += take;
            _size += take;
            data = data[take..];
            if (_pendingLength == _o.ChunkSize)
            {
                EmitChunk(_pending.AsSpan(0, _pendingLength));
                _pendingLength = 0;
            }
        }
    }

    public void Finish(byte[]? md5, byte[]? sha1, byte[]? sha256)
    {
        if (_finished)
        {
            return;
        }
        _finished = true;
        if (_pendingLength > 0)
        {
            EmitChunk(_pending.AsSpan(0, _pendingLength));
            _pendingLength = 0;
        }
        FlushBevy();

        // Map: one range covering the whole stream. Range = map_offset u64, length u64,
        // target_offset u64, target_id u32 (little-endian); idx lists targets by id.
        var range = new byte[28];
        BitConverter.TryWriteBytes(range.AsSpan(0), 0L);
        BitConverter.TryWriteBytes(range.AsSpan(8), _size);
        BitConverter.TryWriteBytes(range.AsSpan(16), 0L);
        BitConverter.TryWriteBytes(range.AsSpan(24), 0u);
        WriteMember(MemberName(MapUrn, "map"), range);
        WriteMember(MemberName(MapUrn, "idx"), Encoding.UTF8.GetBytes(StreamUrn + "\n"));

        WriteMember("information.turtle", Encoding.UTF8.GetBytes(Turtle(md5, sha1, sha256)));

        _zip.Dispose();
        _file.Flush();
        _file.Dispose();
    }

    public void Dispose()
    {
        try { _zip.Dispose(); } catch { }
        _file.Dispose();
    }

    // ---- chunks and bevies --------------------------------------------------------------------

    private void EmitChunk(ReadOnlySpan<byte> plain)
    {
        _bevyIndex.Add((uint)_bevy.Length);
        if (_o.Compress)
        {
            // With zlib declared every chunk is zlib — readers do not probe per chunk.
            using var z = new ZLibStream(_bevy, CompressionLevel.Optimal, leaveOpen: true);
            z.Write(plain);
        }
        else
        {
            _bevy.Write(plain);
        }
        if (_bevyIndex.Count >= _o.ChunksPerSegment)
        {
            FlushBevy();
        }
        if (_bevy.Length > int.MaxValue - (1 << 25))
        {
            throw new InvalidOperationException("AFF4: bevy exceeds the 32-bit index; lower chunks per segment.");
        }
    }

    private void FlushBevy()
    {
        if (_bevyIndex.Count == 0)
        {
            return;
        }
        var name = _bevyNumber.ToString("D8", CultureInfo.InvariantCulture);
        // Standard index: one { uint64 offset; uint32 length; } per chunk (the uint32 offset
        // list is the pre-standard layout, which the reader still accepts).
        var index = new byte[_bevyIndex.Count * 12];
        for (int i = 0; i < _bevyIndex.Count; i++)
        {
            var end = i + 1 < _bevyIndex.Count ? _bevyIndex[i + 1] : (uint)_bevy.Length;
            BitConverter.TryWriteBytes(index.AsSpan(i * 12), (ulong)_bevyIndex[i]);
            BitConverter.TryWriteBytes(index.AsSpan(i * 12 + 8), end - _bevyIndex[i]);
        }
        WriteMember(MemberName(StreamUrn, name + ".index"), index);
        WriteMember(MemberName(StreamUrn, name), _bevy.GetBuffer().AsSpan(0, (int)_bevy.Length));
        _bevy.SetLength(0);
        _bevyIndex.Clear();
        _bevyNumber++;
    }

    private void WriteMember(string name, ReadOnlySpan<byte> data)
    {
        // Stored: chunk payloads are already compressed, and libaff4 reads stored members fastest.
        var entry = _zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = entry.Open();
        s.Write(data);
    }

    /// <summary>v1.0 member naming: the object URN percent-encoded, then a plain path segment.</summary>
    public static string MemberName(string urn, string segment) => Uri.EscapeDataString(urn) + "/" + segment;

    // ---- RDF ----------------------------------------------------------------------------------

    private string Turtle(byte[]? md5, byte[]? sha1, byte[]? sha256)
    {
        var sb = new StringBuilder();
        sb.Append("@prefix aff4: <http://aff4.org/Schema#> .\n");
        sb.Append("@prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .\n");
        sb.Append("@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .\n\n");

        sb.Append('<').Append(VolumeUrn).Append(">\n");
        sb.Append("    a aff4:ZipVolume ;\n");
        sb.Append("    aff4:creationTime \"").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture)).Append("\"^^xsd:dateTime .\n\n");

        sb.Append('<').Append(ImageUrn).Append(">\n");
        sb.Append("    a aff4:DiskImage, aff4:Image, aff4:ContiguousImage ;\n");
        sb.Append("    aff4:dataStream <").Append(MapUrn).Append("> ;\n");
        sb.Append("    aff4:size \"").Append(_size).Append("\"^^xsd:long ;\n");
        sb.Append("    aff4:stored <").Append(VolumeUrn).Append("> ;\n");
        if (md5 is not null) sb.Append("    aff4:hash \"").Append(Convert.ToHexStringLower(md5)).Append("\"^^aff4:MD5 ;\n");
        if (sha1 is not null) sb.Append("    aff4:hash \"").Append(Convert.ToHexStringLower(sha1)).Append("\"^^aff4:SHA1 ;\n");
        if (sha256 is not null) sb.Append("    aff4:hash \"").Append(Convert.ToHexStringLower(sha256)).Append("\"^^aff4:SHA256 ;\n");
        Literal(sb, "aff4:caseNumber", _o.CaseNumber);
        Literal(sb, "aff4:evidenceNumber", _o.EvidenceNumber);
        Literal(sb, "aff4:examiner", _o.Examiner);
        Literal(sb, "aff4:caseDescription", _o.Description);
        Literal(sb, "aff4:caseNotes", _o.Notes);
        Literal(sb, "aff4:acquisitionSource", _o.Source);
        Literal(sb, "aff4:acquisitionTool", _o.Tool);
        sb.Append("    aff4:acquisitionTime \"").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture)).Append("\"^^xsd:dateTime .\n\n");

        sb.Append('<').Append(MapUrn).Append(">\n");
        sb.Append("    a aff4:Map ;\n");
        sb.Append("    aff4:dependentStream <").Append(StreamUrn).Append("> ;\n");
        sb.Append("    aff4:size \"").Append(_size).Append("\"^^xsd:long ;\n");
        sb.Append("    aff4:stored <").Append(VolumeUrn).Append("> .\n\n");

        sb.Append('<').Append(StreamUrn).Append(">\n");
        sb.Append("    a aff4:ImageStream ;\n");
        sb.Append("    aff4:chunkSize \"").Append(_o.ChunkSize).Append("\"^^xsd:int ;\n");
        sb.Append("    aff4:chunksInSegment \"").Append(_o.ChunksPerSegment).Append("\"^^xsd:int ;\n");
        sb.Append("    aff4:compressionMethod <").Append(_o.Compress ? ZlibCompression : StoredCompression).Append("> ;\n");
        sb.Append("    aff4:size \"").Append(_size).Append("\"^^xsd:long ;\n");
        sb.Append("    aff4:stored <").Append(VolumeUrn).Append("> ;\n");
        sb.Append("    aff4:version \"1\"^^xsd:int .\n");
        return sb.ToString();
    }

    private static void Literal(StringBuilder sb, string predicate, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        sb.Append("    ").Append(predicate).Append(" \"").Append(Escape(value)).Append("\" ;\n");
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    private static string Clean(string s) => s.Replace('\n', ' ').Replace('\r', ' ');
}
