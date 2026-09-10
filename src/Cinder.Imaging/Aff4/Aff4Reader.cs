using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Cinder.Imaging.Aff4;

/// <summary>
/// Reads AFF4 physical-image containers: the ZIP volume, its <c>information.turtle</c>, the
/// <c>Image → Map → ImageStream</c> object chain, and bevies in the standard (uint64 offset +
/// uint32 length) or pre-standard (uint32 offset list) index layout with zlib, snappy, LZ4 or
/// stored chunks. Covers what
/// Cinder, pyaff4, libaff4 and Evimetry write for disk images; logical (file-per-object)
/// containers are not in scope.
///
/// <para>The turtle parser is deliberately small — subjects, predicate lists, IRIs, prefixed
/// names, literals with datatypes — because that is all these files contain, and a full RDF
/// stack would be a large dependency for it. Everything read from the container is treated
/// as untrusted: sizes are bounded, offsets are range-checked, and a chunk that fails to
/// decode is zero-filled and recorded in <see cref="DamagedChunks"/> rather than aborting.</para>
/// </summary>
public sealed class Aff4Reader : IDisposable
{
    private const int MaxChunkSize = 1 << 24;
    private const long MaxBevyBytes = 1L << 31;

    private readonly ZipArchive _zip;
    private readonly FileStream _file;
    private readonly List<(string S, string P, string O, bool Literal)> _triples = [];
    private readonly Dictionary<string, ImageStreamInfo> _streams = new(StringComparer.Ordinal);
    private readonly List<int> _damaged = [];
    private readonly int _minor;

    private Aff4Reader(FileStream file, ZipArchive zip)
    {
        _file = file;
        _zip = zip;

        VolumeUrn = ReadText("container.description")?.Trim() ?? zip.Comment?.Trim() ?? "";
        var version = ReadText("version.txt") ?? "";
        var m = Regex.Match(version, @"minor\s*=\s*(\d+)");
        _minor = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

        var turtle = ReadText("information.turtle") ?? throw new InvalidDataException("AFF4: no information.turtle in the container.");
        ParseTurtle(turtle);

        // Images: anything typed aff4:Image (or DiskImage) with a dataStream; fall back to a
        // bare Map or ImageStream so a container without the Image object still opens.
        Images = _triples.Where(t => t.P == Aff4Type && t.O is Aff4Image or Aff4DiskImage or Aff4ContiguousImage)
            .Select(t => t.S).Distinct().Where(s => Get(s, "dataStream") is not null).ToList();
        if (Images.Count == 0)
        {
            Images = _triples.Where(t => t.P == Aff4Type && t.O == Aff4Map).Select(t => t.S).Distinct().ToList();
        }
        if (Images.Count == 0)
        {
            Images = _triples.Where(t => t.P == Aff4Type && t.O == Aff4ImageStream).Select(t => t.S).Distinct().ToList();
        }
        if (Images.Count == 0)
        {
            throw new InvalidDataException("AFF4: the container describes no image, map or image stream.");
        }
        ImageUrn = Images[0];
        Ranges = ResolveRanges(ImageUrn, out var size);
        Size = size;

        foreach (var (_, p, o, lit) in _triples.Where(t => t.S == ImageUrn))
        {
            if (!lit) continue;
            var (value, type) = SplitLiteral(o);
            if (p == Aff4 + "hash")
            {
                if (type == Aff4 + "MD5") RecordedMd5 = value.ToLowerInvariant();
                else if (type == Aff4 + "SHA1") RecordedSha1 = value.ToLowerInvariant();
                else if (type == Aff4 + "SHA256") RecordedSha256 = value.ToLowerInvariant();
            }
            else if (p.StartsWith(Aff4, StringComparison.Ordinal))
            {
                Metadata[p[Aff4.Length..]] = value;
            }
        }
    }

    public static bool IsAff4(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) < 4 || magic[0] != 'P' || magic[1] != 'K' || magic[2] != 3 || magic[3] != 4)
            {
                return false;
            }
            fs.Position = 0;
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
            return zip.GetEntry("container.description") is not null || zip.GetEntry("information.turtle") is not null;
        }
        catch { return false; }
    }

    public static Aff4Reader Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        try
        {
            var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
            return new Aff4Reader(fs, zip);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public string VolumeUrn { get; }
    public string ImageUrn { get; }
    public IReadOnlyList<string> Images { get; }
    public long Size { get; }
    public string? RecordedMd5 { get; }
    public string? RecordedSha1 { get; }
    public string? RecordedSha256 { get; }
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<int> DamagedChunks => _damaged;
    internal IReadOnlyList<MapRange> Ranges { get; }

    public Stream OpenStream() => new Aff4Stream(this, ownsReader: false);
    internal Stream OpenOwningStream() => new Aff4Stream(this, ownsReader: true);

    public void Dispose()
    {
        _zip.Dispose();
        _file.Dispose();
    }

    // ---- object graph -------------------------------------------------------------------------

    internal sealed record MapRange(long MapOffset, long Length, long TargetOffset, ImageStreamInfo? Target);

    internal sealed class ImageStreamInfo
    {
        public required string Urn { get; init; }
        public required int ChunkSize { get; init; }
        public required int ChunksPerSegment { get; init; }
        public required string Compression { get; init; }
        public required long Size { get; init; }
        public int LoadedBevy = -1;
        public byte[]? BevyData;
        public long[]? BevyOffsets;
        public int[]? BevyLengths;
    }

    private List<MapRange> ResolveRanges(string imageUrn, out long size)
    {
        var target = Get(imageUrn, "dataStream") ?? imageUrn;
        var types = _triples.Where(t => t.S == target && t.P == Aff4Type).Select(t => t.O).ToHashSet(StringComparer.Ordinal);
        size = ParseLong(Get(imageUrn, "size")) ?? ParseLong(Get(target, "size")) ?? 0;

        if (types.Contains(Aff4Map))
        {
            var ranges = ReadMap(target);
            if (size == 0 && ranges.Count > 0)
            {
                size = ranges[^1].MapOffset + ranges[^1].Length;
            }
            return ranges;
        }

        var stream = StreamInfo(target) ?? throw new InvalidDataException($"AFF4: {target} is neither a map nor an image stream.");
        if (size == 0) size = stream.Size;
        return [new MapRange(0, stream.Size, 0, stream)];
    }

    private List<MapRange> ReadMap(string mapUrn)
    {
        var idx = ReadText(Member(mapUrn, "idx")) ?? ReadText(MemberRaw(mapUrn, "idx")) ?? "";
        var targets = idx.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
        var raw = ReadBytes(Member(mapUrn, "map")) ?? ReadBytes(MemberRaw(mapUrn, "map"))
            ?? throw new InvalidDataException("AFF4: map has no range data.");
        if (raw.Length % 28 != 0)
        {
            throw new InvalidDataException("AFF4: map range table has an unexpected size.");
        }

        var ranges = new List<MapRange>(raw.Length / 28);
        for (int i = 0; i + 28 <= raw.Length; i += 28)
        {
            var mapOffset = BitConverter.ToInt64(raw, i);
            var length = BitConverter.ToInt64(raw, i + 8);
            var targetOffset = BitConverter.ToInt64(raw, i + 16);
            var targetId = BitConverter.ToUInt32(raw, i + 24);
            if (mapOffset < 0 || length < 0 || targetOffset < 0)
            {
                throw new InvalidDataException("AFF4: map range with negative offsets.");
            }
            ImageStreamInfo? target = null;
            if (targetId < targets.Count)
            {
                var t = targets[(int)targetId];
                if (!IsZeroTarget(t))
                {
                    target = StreamInfo(t);   // null → treated as unreadable (zeros)
                }
            }
            ranges.Add(new MapRange(mapOffset, length, targetOffset, target));
        }
        ranges.Sort((a, b) => a.MapOffset.CompareTo(b.MapOffset));
        return ranges;
    }

    private static bool IsZeroTarget(string t) =>
        t.EndsWith("Zero", StringComparison.Ordinal) || t.EndsWith("UnknownData", StringComparison.Ordinal) ||
        t.EndsWith("UnreadableData", StringComparison.Ordinal) || t == "aff4:Zero";

    private ImageStreamInfo? StreamInfo(string urn)
    {
        if (_streams.TryGetValue(urn, out var cached))
        {
            return cached;
        }
        var chunkSize = (int)(ParseLong(Get(urn, "chunkSize")) ?? 32 * 1024);
        var perSegment = (int)(ParseLong(Get(urn, "chunksInSegment")) ?? 2048);
        if (chunkSize is < 512 or > MaxChunkSize || perSegment is < 1 or > 1 << 20)
        {
            throw new InvalidDataException($"AFF4: {urn} declares implausible chunk geometry ({chunkSize} × {perSegment}).");
        }
        var compression = Get(urn, "compressionMethod") ?? Aff4Writer.StoredCompression;
        var size = ParseLong(Get(urn, "size")) ?? 0;
        if (_zip.GetEntry(Member(urn, "00000000")) is null && _zip.GetEntry(MemberRaw(urn, "00000000")) is null && size > 0)
        {
            return null;   // referenced but not stored here (a foreign container)
        }
        var info = new ImageStreamInfo { Urn = urn, ChunkSize = chunkSize, ChunksPerSegment = perSegment, Compression = compression, Size = size };
        _streams[urn] = info;
        return info;
    }

    // ---- chunk access -------------------------------------------------------------------------

    /// <summary>Copies bytes [offset, offset+count) of an image stream into <paramref name="dest"/>; zero-fills past its end.</summary>
    internal int ReadStream(ImageStreamInfo s, long offset, Span<byte> dest)
    {
        var done = 0;
        while (done < dest.Length)
        {
            var pos = offset + done;
            if (pos >= s.Size)
            {
                dest[done..].Clear();
                return dest.Length;
            }
            var chunkIndex = (int)(pos / s.ChunkSize);
            var within = (int)(pos % s.ChunkSize);
            var chunk = ReadChunk(s, chunkIndex);
            var n = Math.Min(dest.Length - done, chunk.Length - within);
            if (n <= 0)
            {
                dest[done..].Clear();
                return dest.Length;
            }
            chunk.AsSpan(within, n).CopyTo(dest[done..]);
            done += n;
        }
        return done;
    }

    private byte[] ReadChunk(ImageStreamInfo s, int chunkIndex)
    {
        var bevy = chunkIndex / s.ChunksPerSegment;
        var inBevy = chunkIndex % s.ChunksPerSegment;
        if (s.LoadedBevy != bevy)
        {
            LoadBevy(s, bevy);
        }
        var expected = (int)Math.Min(s.ChunkSize, s.Size - (long)chunkIndex * s.ChunkSize);
        if (expected <= 0)
        {
            return [];
        }
        if (s.BevyData is null || s.BevyOffsets is null || inBevy >= s.BevyOffsets.Length)
        {
            return Damaged(chunkIndex, expected);
        }

        var start = s.BevyOffsets[inBevy];
        var length = s.BevyLengths is not null
            ? s.BevyLengths[inBevy]
            : (int)((inBevy + 1 < s.BevyOffsets.Length ? s.BevyOffsets[inBevy + 1] : s.BevyData.Length) - start);
        if (start < 0 || length < 0 || start + length > s.BevyData.Length)
        {
            return Damaged(chunkIndex, expected);
        }
        var raw = s.BevyData.AsSpan((int)start, length);
        try
        {
            var outBuf = new byte[expected];
            var produced = s.Compression switch
            {
                Aff4Writer.StoredCompression or "http://aff4.org/Schema#NullCompressor" or "https://aff4.org/Schema#NullCompressor" => Copy(raw, outBuf),
                Aff4Writer.ZlibCompression or "http://www.ietf.org/rfc/rfc1950.txt" => Inflate(raw, outBuf),
                "https://code.google.com/p/snappy/" or "http://code.google.com/p/snappy/" => SnappyDecoder.Decode(raw, outBuf),
                "https://code.google.com/p/lz4/" or "http://code.google.com/p/lz4/" => Lz4Decoder.Decode(raw, outBuf),
                _ => throw new NotSupportedException($"AFF4: compression method {s.Compression} is not supported."),
            };
            if (produced < expected)
            {
                // A stored chunk may legitimately be exactly the raw bytes even under a
                // compressing stream (libaff4 does this for incompressible data).
                if (raw.Length == expected)
                {
                    raw.CopyTo(outBuf);
                    return outBuf;
                }
                RecordDamage(chunkIndex);
                outBuf.AsSpan(produced).Clear();
            }
            return outBuf;
        }
        catch (NotSupportedException) { throw; }
        catch (Exception)
        {
            if (raw.Length == expected)
            {
                return raw.ToArray();
            }
            return Damaged(chunkIndex, expected);
        }
    }

    private static int Copy(ReadOnlySpan<byte> raw, byte[] outBuf)
    {
        var n = Math.Min(raw.Length, outBuf.Length);
        raw[..n].CopyTo(outBuf);
        return n;
    }

    private static int Inflate(ReadOnlySpan<byte> raw, byte[] outBuf)
    {
        using var ms = new MemoryStream(raw.ToArray());
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        var filled = 0;
        while (filled < outBuf.Length)
        {
            var n = z.Read(outBuf, filled, outBuf.Length - filled);
            if (n <= 0) break;
            filled += n;
        }
        return filled;
    }

    private void LoadBevy(ImageStreamInfo s, int bevy)
    {
        s.LoadedBevy = bevy;
        s.BevyData = null;
        s.BevyOffsets = null;
        s.BevyLengths = null;

        var name = bevy.ToString("D8", CultureInfo.InvariantCulture);
        var data = ReadBytes(Member(s.Urn, name), MaxBevyBytes) ?? ReadBytes(MemberRaw(s.Urn, name), MaxBevyBytes);
        var index = ReadBytes(Member(s.Urn, name + ".index")) ?? ReadBytes(MemberRaw(s.Urn, name + ".index"));
        if (data is null || index is null)
        {
            return;
        }
        s.BevyData = data;

        // Standard layout is { uint64 offset; uint32 length; } per chunk; pre-standard
        // (Evimetry / early pyaff4) is a uint32 offset list. Twelve is a multiple of four, so
        // the length alone cannot tell them apart — decode as standard and keep it only if
        // every entry lies inside the bevy in order, which a uint32 list never does.
        if (index.Length % 12 == 0 && index.Length > 0 && TryStandardIndex(index, data.Length, out var offsets, out var lengths))
        {
            s.BevyOffsets = offsets;
            s.BevyLengths = lengths;
        }
        else
        {
            var n = index.Length / 4;
            s.BevyOffsets = new long[n];
            for (int i = 0; i < n; i++)
            {
                s.BevyOffsets[i] = BitConverter.ToUInt32(index, i * 4);
            }
        }
    }

    private static bool TryStandardIndex(byte[] index, long bevyLength, out long[] offsets, out int[] lengths)
    {
        var n = index.Length / 12;
        offsets = new long[n];
        lengths = new int[n];
        long previous = -1;
        for (int i = 0; i < n; i++)
        {
            var off = BitConverter.ToInt64(index, i * 12);
            var len = BitConverter.ToInt32(index, i * 12 + 8);
            if (off < previous || len < 0 || off + len > bevyLength)
            {
                return false;
            }
            offsets[i] = off;
            lengths[i] = len;
            previous = off;
        }
        return true;
    }

    private byte[] Damaged(int chunkIndex, int length)
    {
        RecordDamage(chunkIndex);
        return new byte[length];
    }

    private void RecordDamage(int chunkIndex)
    {
        if (_damaged.Count < 100_000 && (_damaged.Count == 0 || _damaged[^1] != chunkIndex))
        {
            _damaged.Add(chunkIndex);
        }
    }

    // ---- members ---------------------------------------------------------------------------

    private static string Member(string urn, string segment) => Aff4Writer.MemberName(urn, segment);
    private static string MemberRaw(string urn, string segment) => urn + "/" + segment;

    private string? ReadText(string name)
    {
        var b = ReadBytes(name, 64 * 1024 * 1024);
        return b is null ? null : Encoding.UTF8.GetString(b);
    }

    private byte[]? ReadBytes(string name, long limit = 64L * 1024 * 1024)
    {
        var entry = _zip.GetEntry(name);
        if (entry is null)
        {
            return null;
        }
        if (entry.Length > limit)
        {
            throw new InvalidDataException($"AFF4: member {name} is {entry.Length:N0} bytes, above the {limit:N0}-byte ceiling.");
        }
        using var s = entry.Open();
        var buf = new byte[entry.Length];
        var filled = 0;
        while (filled < buf.Length)
        {
            var n = s.Read(buf, filled, buf.Length - filled);
            if (n <= 0) break;
            filled += n;
        }
        return filled == buf.Length ? buf : buf[..filled];
    }

    // ---- turtle ------------------------------------------------------------------------------

    private const string Aff4 = "http://aff4.org/Schema#";
    private const string Aff4Type = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
    private const string Aff4Image = Aff4 + "Image";
    private const string Aff4DiskImage = Aff4 + "DiskImage";
    private const string Aff4ContiguousImage = Aff4 + "ContiguousImage";
    private const string Aff4Map = Aff4 + "Map";
    private const string Aff4ImageStream = Aff4 + "ImageStream";

    private string? Get(string subject, string aff4Predicate)
    {
        var p = Aff4 + aff4Predicate;
        foreach (var t in _triples)
        {
            if (t.S == subject && t.P == p)
            {
                return t.Literal ? SplitLiteral(t.O).Value : t.O;
            }
        }
        return null;
    }

    private static (string Value, string? Type) SplitLiteral(string o)
    {
        var i = o.IndexOf("^^", StringComparison.Ordinal);
        return i < 0 ? (o, null) : (o[..i], o[(i + 2)..]);
    }

    private static long? ParseLong(string? s) =>
        s is not null && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private void ParseTurtle(string text)
    {
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rdf"] = "http://www.w3.org/1999/02/22-rdf-syntax-ns#",
            ["xsd"] = "http://www.w3.org/2001/XMLSchema#",
            ["aff4"] = Aff4,
        };
        var tokens = Tokenize(text);
        var i = 0;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == Tok.Word && (t.Text == "@prefix" || t.Text.Equals("PREFIX", StringComparison.OrdinalIgnoreCase)))
            {
                if (i + 2 < tokens.Count)
                {
                    var name = tokens[i + 1].Text.TrimEnd(':');
                    prefixes[name] = tokens[i + 2].Text;
                }
                i += 3;
                if (i < tokens.Count && tokens[i].Kind == Tok.Dot) i++;
                continue;
            }
            if (t.Kind == Tok.Word && t.Text == "@base")
            {
                i += 2;
                if (i < tokens.Count && tokens[i].Kind == Tok.Dot) i++;
                continue;
            }

            // subject predicateObjectList .
            var subject = Expand(t, prefixes);
            i++;
            string? predicate = null;
            while (i < tokens.Count && tokens[i].Kind != Tok.Dot)
            {
                var tk = tokens[i];
                if (tk.Kind == Tok.Semi) { predicate = null; i++; continue; }
                if (tk.Kind == Tok.Comma) { i++; continue; }
                if (predicate is null)
                {
                    predicate = tk.Kind == Tok.Word && tk.Text == "a" ? Aff4Type : Expand(tk, prefixes);
                    i++;
                    continue;
                }
                if (tk.Kind == Tok.Literal)
                {
                    var value = tk.Text;
                    var type = tk.Datatype is null ? null : Expand(new Token(tk.Datatype.StartsWith('<') ? Tok.Iri : Tok.Word, tk.Datatype.Trim('<', '>')), prefixes);
                    _triples.Add((subject, predicate, type is null ? value : value + "^^" + type, true));
                }
                else if (tk.Kind == Tok.Number)
                {
                    _triples.Add((subject, predicate, tk.Text, true));
                }
                else
                {
                    _triples.Add((subject, predicate, Expand(tk, prefixes), false));
                }
                i++;
            }
            i++;   // the dot
        }
    }

    private static string Expand(Token t, Dictionary<string, string> prefixes)
    {
        if (t.Kind == Tok.Iri)
        {
            return t.Text;
        }
        var colon = t.Text.IndexOf(':');
        if (colon > 0 && prefixes.TryGetValue(t.Text[..colon], out var ns))
        {
            return ns + t.Text[(colon + 1)..];
        }
        return t.Text;
    }

    private enum Tok { Iri, Word, Literal, Number, Dot, Semi, Comma }

    private sealed record Token(Tok Kind, string Text, string? Datatype = null);

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '#') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '<')
            {
                var end = s.IndexOf('>', i + 1);
                if (end < 0) break;
                tokens.Add(new Token(Tok.Iri, s[(i + 1)..end]));
                i = end + 1;
                continue;
            }
            if (c == '"')
            {
                var sb = new StringBuilder();
                var j = i + 1;
                var triple = j + 1 < s.Length && s[j] == '"' && s[j + 1] == '"';
                if (triple) j += 2;
                while (j < s.Length)
                {
                    if (s[j] == '\\' && j + 1 < s.Length)
                    {
                        sb.Append(s[j + 1] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', var x => x });
                        j += 2;
                        continue;
                    }
                    if (s[j] == '"' && (!triple || (j + 2 < s.Length && s[j + 1] == '"' && s[j + 2] == '"')))
                    {
                        j += triple ? 3 : 1;
                        break;
                    }
                    sb.Append(s[j]);
                    j++;
                }
                string? datatype = null;
                if (j + 1 < s.Length && s[j] == '^' && s[j + 1] == '^')
                {
                    j += 2;
                    var start = j;
                    if (j < s.Length && s[j] == '<')
                    {
                        var end = s.IndexOf('>', j);
                        datatype = s[j..(end + 1)];
                        j = end + 1;
                    }
                    else
                    {
                        while (j < s.Length && !char.IsWhiteSpace(s[j]) && s[j] != ';' && s[j] != ',' && !(s[j] == '.' && (j + 1 >= s.Length || char.IsWhiteSpace(s[j + 1])))) j++;
                        datatype = s[start..j];
                    }
                }
                else if (j < s.Length && s[j] == '@')
                {
                    while (j < s.Length && !char.IsWhiteSpace(s[j]) && s[j] != ';' && s[j] != ',') j++;
                }
                tokens.Add(new Token(Tok.Literal, sb.ToString(), datatype));
                i = j;
                continue;
            }
            if (c == ';') { tokens.Add(new Token(Tok.Semi, ";")); i++; continue; }
            if (c == ',') { tokens.Add(new Token(Tok.Comma, ",")); i++; continue; }
            if (c == '.' && (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1])))
            {
                tokens.Add(new Token(Tok.Dot, "."));
                i++;
                continue;
            }
            var st = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != ';' && s[i] != ',' && !(s[i] == '.' && (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1])))) i++;
            var word = s[st..i];
            if (word.Length > 0 && (char.IsDigit(word[0]) || (word[0] == '-' && word.Length > 1)))
            {
                tokens.Add(new Token(Tok.Number, word));
            }
            else
            {
                tokens.Add(new Token(Tok.Word, word));
            }
        }
        return tokens;
    }

    // ---- stream -----------------------------------------------------------------------------

    private sealed class Aff4Stream(Aff4Reader reader, bool ownsReader) : Stream
    {
        private long _pos;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => reader.Size;
        public override long Position { get => _pos; set => _pos = value; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_pos >= reader.Size)
            {
                return 0;
            }
            var n = (int)Math.Min(buffer.Length, reader.Size - _pos);
            var dest = buffer[..n];
            var done = 0;
            while (done < n)
            {
                var pos = _pos + done;
                var r = FindRange(pos);
                if (r is null)
                {
                    // Gap in the map: zeros up to the next range.
                    var next = reader.Ranges.FirstOrDefault(x => x.MapOffset > pos);
                    var gap = (int)Math.Min(n - done, (next?.MapOffset ?? reader.Size) - pos);
                    dest.Slice(done, gap).Clear();
                    done += gap;
                    continue;
                }
                var within = pos - r.MapOffset;
                var take = (int)Math.Min(n - done, r.Length - within);
                if (r.Target is null)
                {
                    dest.Slice(done, take).Clear();
                }
                else
                {
                    reader.ReadStream(r.Target, r.TargetOffset + within, dest.Slice(done, take));
                }
                done += take;
            }
            _pos += done;
            return done;
        }

        private MapRange? FindRange(long pos)
        {
            var ranges = reader.Ranges;
            int lo = 0, hi = ranges.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var r = ranges[mid];
                if (pos < r.MapOffset) hi = mid - 1;
                else if (pos >= r.MapOffset + r.Length) lo = mid + 1;
                else return r;
            }
            return null;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                _ => reader.Size + offset,
            };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && ownsReader)
            {
                reader.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>Snappy raw-block decoder (the format pyaff4 / libaff4 / Evimetry use per chunk).</summary>
internal static class SnappyDecoder
{
    public static int Decode(ReadOnlySpan<byte> src, byte[] dst)
    {
        var i = 0;
        // Preamble: varint uncompressed length.
        long declared = 0;
        var shift = 0;
        while (i < src.Length)
        {
            var b = src[i++];
            declared |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 35) throw new InvalidDataException("snappy: bad length varint");
        }
        var o = 0;
        var limit = (int)Math.Min(dst.Length, declared);
        while (i < src.Length && o < limit)
        {
            var tag = src[i++];
            switch (tag & 3)
            {
                case 0:
                    {
                        var len = tag >> 2;
                        if (len >= 60)
                        {
                            var extra = len - 59;
                            if (i + extra > src.Length) throw new InvalidDataException("snappy: truncated literal length");
                            len = 0;
                            for (int k = 0; k < extra; k++) len |= src[i + k] << (8 * k);
                            i += extra;
                        }
                        len += 1;
                        if (i + len > src.Length || o + len > dst.Length) throw new InvalidDataException("snappy: literal overruns");
                        src.Slice(i, len).CopyTo(dst.AsSpan(o));
                        i += len;
                        o += len;
                        break;
                    }
                case 1:
                    {
                        var len = 4 + ((tag >> 2) & 7);
                        if (i >= src.Length) throw new InvalidDataException("snappy: truncated copy");
                        var offset = ((tag >> 5) << 8) | src[i++];
                        o = CopyBack(dst, o, offset, len);
                        break;
                    }
                case 2:
                    {
                        var len = 1 + (tag >> 2);
                        if (i + 2 > src.Length) throw new InvalidDataException("snappy: truncated copy");
                        var offset = src[i] | (src[i + 1] << 8);
                        i += 2;
                        o = CopyBack(dst, o, offset, len);
                        break;
                    }
                default:
                    {
                        var len = 1 + (tag >> 2);
                        if (i + 4 > src.Length) throw new InvalidDataException("snappy: truncated copy");
                        var offset = (int)BitConverter.ToUInt32(src.Slice(i, 4));
                        i += 4;
                        o = CopyBack(dst, o, offset, len);
                        break;
                    }
            }
        }
        return o;
    }

    private static int CopyBack(byte[] dst, int o, int offset, int len)
    {
        if (offset <= 0 || offset > o || o + len > dst.Length) throw new InvalidDataException("snappy: bad copy");
        for (int k = 0; k < len; k++)
        {
            dst[o + k] = dst[o - offset + k];
        }
        return o + len;
    }
}

/// <summary>LZ4 raw-block decoder (no frame header), as libaff4 stores chunks.</summary>
internal static class Lz4Decoder
{
    public static int Decode(ReadOnlySpan<byte> src, byte[] dst)
    {
        var i = 0;
        var o = 0;
        while (i < src.Length)
        {
            var token = src[i++];
            var literal = token >> 4;
            if (literal == 15)
            {
                byte b;
                do
                {
                    if (i >= src.Length) throw new InvalidDataException("lz4: truncated literal length");
                    b = src[i++];
                    literal += b;
                } while (b == 255);
            }
            if (i + literal > src.Length || o + literal > dst.Length) throw new InvalidDataException("lz4: literal overruns");
            src.Slice(i, literal).CopyTo(dst.AsSpan(o));
            i += literal;
            o += literal;
            if (i >= src.Length)
            {
                break;   // last sequence has no match
            }
            if (i + 2 > src.Length) throw new InvalidDataException("lz4: truncated offset");
            var offset = src[i] | (src[i + 1] << 8);
            i += 2;
            var match = (token & 0xF) + 4;
            if ((token & 0xF) == 15)
            {
                byte b;
                do
                {
                    if (i >= src.Length) throw new InvalidDataException("lz4: truncated match length");
                    b = src[i++];
                    match += b;
                } while (b == 255);
            }
            if (offset <= 0 || offset > o || o + match > dst.Length) throw new InvalidDataException("lz4: bad match");
            for (int k = 0; k < match; k++)
            {
                dst[o + k] = dst[o - offset + k];
            }
            o += match;
        }
        return o;
    }
}
