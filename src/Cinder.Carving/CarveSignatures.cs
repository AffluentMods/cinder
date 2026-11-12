using System.Buffers.Binary;

namespace Cinder.Carving;

/// <summary>
/// Built-in carving rules for the forensically common formats.
///
/// <para>A header pattern alone is weak evidence — "MZ" and "BM" each occur about once in
/// every 64 KiB of compressed data — and a fixed maximum length is no length at all. So every
/// rule here that can do better does: it checks the structure behind the pattern before
/// anything is read (<see cref="CarveSignature.Probe"/> returning
/// <see cref="CarveProbeResult.NotThisFormat"/> drops the hit), and it takes the object's size
/// from the format's own header or by walking its structure. Rules for formats with neither a
/// size nor a trailer are carved up to the next header of the same type or the carver's
/// unsized limit, whichever comes first.</para>
///
/// <para>No two rules share a header. ZIP-based documents (DOCX, XLSX, JAR …) carve as ZIP and
/// every compound file (DOC, XLS, MSG, MSI) as one OLE rule; the container is what the bytes
/// can prove, and carving each one twice only doubled the output.</para>
/// </summary>
public static class CarveSignatures
{
    private const long No = CarveProbeResult.NotThisFormat;
    private const long Unknown = CarveProbeResult.UnknownLength;
    private const long Damaged = CarveProbeResult.Damaged;

    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    public static IReadOnlyList<CarveSignature> Defaults { get; } =
    [
        // ---- images -------------------------------------------------------------------------
        new("JPEG", "jpg", [0xFF, 0xD8, 0xFF], [0xFF, 0xD9], 30 * MiB) { Probe = ProbeJpeg },
        new("PNG", "png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82], 80 * MiB) { FooterRequired = true },
        new("GIF", "gif", "GIF8"u8.ToArray(), [0x00, 0x3B], 10 * MiB) { Probe = ProbeGif, FooterRequired = true },
        new("BMP", "bmp", "BM"u8.ToArray(), null, 50 * MiB) { Probe = ProbeBmp },
        new("TIFF", "tiff", [0x49, 0x49, 0x2A, 0x00], null, 100 * MiB) { Probe = ProbeTiff },
        new("WebP", "webp", "WEBPVP8"u8.ToArray(), null, 50 * MiB) { HeaderOffset = 8, Probe = ProbeRiff },
        new("HEIC", "heic", "ftypheic"u8.ToArray(), null, 100 * MiB) { HeaderOffset = 4, Probe = ProbeIsoBmff },

        // ---- documents ----------------------------------------------------------------------
        new("PDF", "pdf", "%PDF-"u8.ToArray(), "%%EOF"u8.ToArray(), 100 * MiB) { Probe = ProbePdf, FooterRequired = true },
        new("RTF", "rtf", "{\\rtf"u8.ToArray(), null, 50 * MiB) { Probe = ProbeRtf },
        new("OLE Compound (DOC/XLS/PPT/MSG/MSI)", "doc", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], null, 100 * MiB) { Probe = ProbeOle },

        // ---- archives -----------------------------------------------------------------------
        // The end-of-central-directory record is the footer; 18 more bytes complete it.
        new("ZIP (incl. DOCX/XLSX/JAR/APK)", "zip", [0x50, 0x4B, 0x03, 0x04], [0x50, 0x4B, 0x05, 0x06], 500 * MiB)
            { Probe = ProbeZip, FooterRequired = true, FooterTrailerBytes = 18 },
        new("RAR", "rar", [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07], null, GiB) { Probe = ProbeRar },
        new("7Z", "7z", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], null, GiB) { Probe = Probe7z },

        // ---- audio / video ------------------------------------------------------------------
        new("MP3", "mp3", "ID3"u8.ToArray(), null, 50 * MiB) { Probe = ProbeId3 },
        new("MP4", "mp4", "ftyp"u8.ToArray(), null, 4 * GiB) { HeaderOffset = 4, Probe = ProbeMp4 },
        new("WAV", "wav", "WAVEfmt "u8.ToArray(), null, 500 * MiB) { HeaderOffset = 8, Probe = ProbeRiff },
        new("AVI", "avi", "AVI LIST"u8.ToArray(), null, 4 * GiB) { HeaderOffset = 8, Probe = ProbeRiff },
        new("Matroska/WebM", "mkv", [0x1A, 0x45, 0xDF, 0xA3], null, 10 * GiB) { Probe = ProbeMatroska },

        // ---- executables --------------------------------------------------------------------
        new("EXE/DLL (PE)", "exe", "MZ"u8.ToArray(), null, 200 * MiB) { Probe = ProbePe },
        new("ELF", "elf", [0x7F, 0x45, 0x4C, 0x46], null, 200 * MiB) { Probe = ProbeElf },

        // ---- databases and Windows artifacts ------------------------------------------------
        new("SQLite", "sqlite", "SQLite format 3\0"u8.ToArray(), null, 10 * GiB) { Probe = ProbeSqlite },
        new("PST", "pst", "!BDN"u8.ToArray(), null, 50 * GiB) { Probe = ProbePst },
        new("Registry hive", "hive", "regf"u8.ToArray(), null, 500 * MiB) { Probe = ProbeRegf },
        new("LNK", "lnk",
            [0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46],
            null, 64 * 1024),
        new("EVTX", "evtx", "ElfFile\0"u8.ToArray(), null, 500 * MiB) { Probe = ProbeEvtx },

        // ---- network captures ---------------------------------------------------------------
        new("PCAP", "pcap", [0xD4, 0xC3, 0xB2, 0xA1], null, 4 * GiB) { Probe = ProbePcap },
        new("PCAPNG", "pcapng", [0x0A, 0x0D, 0x0D, 0x0A], null, 4 * GiB) { Probe = ProbePcapNg },
    ];

    // ---- little helpers ------------------------------------------------------------------------

    private static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
    private static ulong U64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt64LittleEndian(s[o..]);
    private static ushort U16Be(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16BigEndian(s[o..]);
    private static uint U32Be(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32BigEndian(s[o..]);
    private static ulong U64Be(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt64BigEndian(s[o..]);

    /// <summary>A length field as a probe result: positive and representable, or "not this format".</summary>
    private static long AsLength(ulong value) => value is > 0 and <= long.MaxValue / 2 ? (long)value : No;

    private static bool IsFourCc(ReadOnlySpan<byte> t)
    {
        foreach (var b in t)
        {
            if (b is not ((>= 0x20 and <= 0x7E) or 0xA9))
            {
                return false;
            }
        }
        return true;
    }

    // ---- images ----------------------------------------------------------------------------------

    /// <summary>
    /// Walks the marker segments to the scan data and the scan data to the end-of-image
    /// marker. Searching for the first FF D9 instead stops at the end of the embedded EXIF
    /// thumbnail, which is itself a complete JPEG inside an APP1 segment.
    /// </summary>
    internal static long ProbeJpeg(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 4 || h[3] < 0xC0 || h[3] == 0xFF || h[3] is 0xD8 or 0xD9 || h[3] is >= 0xD0 and <= 0xD7)
        {
            return No;
        }

        var limit = src.Limit;
        Span<byte> b = stackalloc byte[2];
        byte[]? scratch = null;
        long pos = 2;
        for (int segments = 0; segments < 8192; segments++)
        {
            if (pos + 2 > limit || !src.TryRead(pos, b))
            {
                return Damaged;
            }
            if (b[0] != 0xFF)
            {
                return Damaged;
            }
            var marker = b[1];
            if (marker == 0xFF)
            {
                pos++;                      // fill byte before a marker
                continue;
            }
            if (marker == 0xD9)
            {
                return pos + 2;             // end of image
            }
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                pos += 2;                   // stand-alone markers carry no length
                continue;
            }
            if (marker < 0xC0)
            {
                return Damaged;
            }
            if (pos + 4 > limit || !src.TryRead(pos + 2, b))
            {
                return Damaged;
            }
            var length = (b[0] << 8) | b[1];
            if (length < 2)
            {
                return Damaged;
            }
            pos += 2 + length;

            if (marker == 0xDA)
            {
                // Entropy-coded data: FF is always followed by 00 (stuffing) or a restart
                // marker, so the next FF xx that is neither is the next real marker.
                scratch ??= new byte[32 * 1024];
                pos = NextJpegMarker(src, pos, limit, scratch);
                if (pos < 0)
                {
                    return Damaged;
                }
            }
        }
        return Damaged;
    }

    private static long NextJpegMarker(CarveSource src, long pos, long limit, byte[] scratch)
    {
        while (pos < limit)
        {
            var want = (int)Math.Min(scratch.Length, limit - pos);
            var got = src.Read(pos, scratch.AsSpan(0, want));
            if (got <= 0)
            {
                return -1;
            }
            var block = scratch.AsSpan(0, got);
            var i = 0;
            var trailingFf = false;
            while (i < got)
            {
                var rel = block[i..].IndexOf((byte)0xFF);
                if (rel < 0)
                {
                    i = got;
                    break;
                }
                i += rel;
                if (i + 1 >= got)
                {
                    trailingFf = true;      // need the next block to see what follows
                    break;
                }
                var next = block[i + 1];
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
                {
                    i += 2;
                    continue;
                }
                if (next == 0xFF)
                {
                    i++;
                    continue;
                }
                return pos + i;
            }

            if (trailingFf)
            {
                if (i == 0)
                {
                    return -1;              // a lone FF is all that is left
                }
                pos += i;
            }
            else
            {
                pos += got;
            }
        }
        return -1;
    }

    private static long ProbeGif(CarveSource src)
    {
        var h = src.Head;
        // "GIF87a" or "GIF89a", followed by the 7-byte logical screen descriptor.
        return h.Length >= 13 && (h[4] == 0x37 || h[4] == 0x39) && h[5] == 0x61 ? Unknown : No;
    }

    private static long ProbeBmp(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 30)
        {
            return No;
        }
        var size = U32(h, 2);
        if (U16(h, 6) != 0 || U16(h, 8) != 0)
        {
            return No;                      // reserved fields
        }
        var pixelOffset = U32(h, 10);
        var dib = U32(h, 14);
        if (dib is not (12 or 40 or 52 or 56 or 64 or 108 or 124))
        {
            return No;
        }
        if (pixelOffset < 14 + dib || pixelOffset > size)
        {
            return No;
        }
        if (dib >= 40)
        {
            var width = BinaryPrimitives.ReadInt32LittleEndian(h[18..]);
            var height = BinaryPrimitives.ReadInt32LittleEndian(h[22..]);
            if (width is <= 0 or > 65535 || height == 0 || height is < -65535 or > 65535)
            {
                return No;
            }
            if (U16(h, 26) != 1 || U16(h, 28) is not (1 or 4 or 8 or 16 or 24 or 32))
            {
                return No;                  // planes, bits per pixel
            }
        }
        return size;
    }

    private static long ProbeTiff(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 8)
        {
            return No;
        }
        var ifd = U32(h, 4);
        if (ifd < 8 || (ifd & 1) != 0)
        {
            return No;                      // IFDs start on a word boundary, after the header
        }
        if (!src.TryReadUInt16LittleEndian(ifd, out var entries) || entries is 0 or > 4096)
        {
            return No;
        }
        return Unknown;                     // TIFF has no overall length field
    }

    /// <summary>RIFF: <c>"RIFF" size32 form</c>; the size counts everything after itself.</summary>
    private static long ProbeRiff(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 12 || !h[..4].SequenceEqual("RIFF"u8))
        {
            return No;
        }
        var size = U32(h, 4);
        if (size < 4)
        {
            return No;
        }
        return 8L + size + (size & 1);      // chunks are padded to an even length
    }

    // ---- ISO base media (MP4, MOV, HEIC) ------------------------------------------------------

    private static long ProbeMp4(CarveSource src)
    {
        var h = src.Head;
        // HEIC has its own rule; without this both would carve the same object.
        return h.Length >= 12 && h.Slice(8, 4).SequenceEqual("heic"u8) ? No : ProbeIsoBmff(src);
    }

    /// <summary>Sums the top-level boxes. Every box starts with its own size, so the file's length is their total.</summary>
    internal static long ProbeIsoBmff(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 16)
        {
            return No;
        }
        var first = U32Be(h, 0);
        if (first < 16 || first > 4096 || (first & 3) != 0 || !IsFourCc(h.Slice(8, 4)))
        {
            return No;                      // an ftyp box is small and lists 4-byte brands
        }

        Span<byte> b = stackalloc byte[16];
        long pos = 0;
        var boxes = 0;
        while (boxes < 1_000_000)
        {
            if (!src.TryRead(pos, b[..8]))
            {
                break;                      // end of input: the previous box was the last
            }
            ulong size = U32Be(b, 0);
            if (!IsTopLevelBox(b.Slice(4, 4)))
            {
                break;                      // not a box: the file ended at pos
            }
            if (size == 1)
            {
                if (!src.TryRead(pos, b))
                {
                    return Damaged;
                }
                size = U64Be(b, 8);
                if (size < 16)
                {
                    return Damaged;
                }
            }
            else if (size == 0)
            {
                // "Extends to the end of the file" — which carving cannot know.
                return boxes >= 1 ? Unknown : No;
            }
            else if (size < 8)
            {
                break;
            }
            if (size > long.MaxValue / 2)
            {
                return Damaged;
            }
            pos += (long)size;
            boxes++;
            if (pos > src.Limit)
            {
                return pos;                 // declared larger than this type may be; the carver reports it
            }
        }
        return boxes >= 2 ? pos : No;       // an ftyp box with nothing after it is not a media file
    }

    /// <summary>
    /// Box types that appear at the top level of an ISO-BMFF / QuickTime file. Any four
    /// printable bytes would pass for a box type about one time in fifty, and the "size"
    /// before them would then extend the object by up to 4 GiB of whatever follows it on disk.
    /// </summary>
    private static bool IsTopLevelBox(ReadOnlySpan<byte> type)
    {
        ReadOnlySpan<byte> known =
            "ftypmoovmdatfreeskipwidemoofmframetauuidpdinstypsidxssixprftemsgpnotudtajunkmeco"u8;
        for (int i = 0; i < known.Length; i += 4)
        {
            if (type.SequenceEqual(known.Slice(i, 4)))
            {
                return true;
            }
        }
        return false;
    }

    // ---- documents and archives ---------------------------------------------------------------

    private static long ProbePdf(CarveSource src)
    {
        var h = src.Head;
        return h.Length >= 8 && char.IsAsciiDigit((char)h[5]) && h[6] == (byte)'.' && char.IsAsciiDigit((char)h[7]) ? Unknown : No;
    }

    /// <summary>An RTF document is one brace group; it ends where its first brace closes.</summary>
    private static long ProbeRtf(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 6 || h[5] != (byte)'1')
        {
            return No;
        }
        var limit = src.Limit;
        var scratch = new byte[32 * 1024];
        long pos = 0;
        var depth = 0;
        var escaped = false;
        while (pos < limit)
        {
            var got = src.Read(pos, scratch.AsSpan(0, (int)Math.Min(scratch.Length, limit - pos)));
            if (got <= 0)
            {
                break;
            }
            for (int i = 0; i < got; i++)
            {
                var c = scratch[i];
                if (escaped)
                {
                    escaped = false;        // \{ \} \\ are literal characters
                }
                else if (c == (byte)'\\')
                {
                    escaped = true;
                }
                else if (c == (byte)'{')
                {
                    depth++;
                }
                else if (c == (byte)'}' && --depth == 0)
                {
                    return pos + i + 1;
                }
            }
            pos += got;
        }
        return Damaged;
    }

    /// <summary>
    /// Compound File Binary. The FAT has one entry per sector, so the last used entry of the
    /// last FAT sector gives the file's size to within a sector.
    /// </summary>
    private static long ProbeOle(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 512 || h[0x1C] != 0xFE || h[0x1D] != 0xFF)
        {
            return No;                      // byte-order mark
        }
        var major = U16(h, 0x1A);
        var shift = U16(h, 0x1E);
        if (!((major == 3 && shift == 9) || (major == 4 && shift == 12)) || U16(h, 0x20) != 6)
        {
            return No;
        }
        var sector = 1 << shift;
        var fatSectors = U32(h, 0x2C);
        if (fatSectors is 0 or > 1_048_576)
        {
            return No;
        }
        var entriesPerSector = sector / 4;
        var sectors = (long)fatSectors * entriesPerSector;      // upper bound

        // The first 109 FAT sector locations are in the header; with more, the bound stands.
        if (fatSectors <= 109)
        {
            var lastFat = U32(h, 0x4C + 4 * ((int)fatSectors - 1));
            var fat = new byte[sector];
            if (lastFat < 0xFFFFFFFA && src.TryRead((lastFat + 1L) * sector, fat))
            {
                var lastUsed = -1;
                for (int i = entriesPerSector - 1; i >= 0; i--)
                {
                    if (U32(fat, i * 4) != 0xFFFFFFFF)
                    {
                        lastUsed = i;
                        break;
                    }
                }
                sectors = (long)(fatSectors - 1) * entriesPerSector + lastUsed + 1;
            }
        }
        return (sectors + 1) * sector;      // + the header sector
    }

    private static long ProbeZip(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 30)
        {
            return No;
        }
        if ((U16(h, 4) & 0xFF) > 63)
        {
            return No;                      // "version needed to extract"
        }
        if (U16(h, 8) is not (0 or 1 or 6 or 8 or 9 or 12 or 14 or 19 or 93 or 95 or 96 or 97 or 98 or 99))
        {
            return No;                      // compression method
        }
        var nameLength = U16(h, 26);
        return nameLength is 0 or > 1024 ? No : Unknown;
    }

    private static long ProbeRar(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 8)
        {
            return No;
        }
        // "Rar!\x1A\x07\x00" (v1.5–4) or "Rar!\x1A\x07\x01\x00" (v5).
        return h[6] == 0x00 || (h[6] == 0x01 && h[7] == 0x00) ? Unknown : No;
    }

    private static long Probe7z(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 32 || h[6] != 0 || h[7] > 9)
        {
            return No;                      // format version 0.x
        }
        var nextHeaderOffset = U64(h, 12);
        var nextHeaderSize = U64(h, 20);
        if (nextHeaderOffset > long.MaxValue / 4 || nextHeaderSize > long.MaxValue / 4)
        {
            return No;
        }
        return 32 + (long)nextHeaderOffset + (long)nextHeaderSize;
    }

    // ---- audio / video ------------------------------------------------------------------------

    private static long ProbeId3(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 10)
        {
            return No;
        }
        // Version 2.2–2.4, no undefined flag bits, and a "syncsafe" size: four 7-bit bytes.
        if (h[3] is < 2 or > 4 || h[4] == 0xFF || (h[5] & 0x0F) != 0)
        {
            return No;
        }
        return (h[6] | h[7] | h[8] | h[9]) < 0x80 ? Unknown : No;
    }

    /// <summary>EBML header, then one Segment whose size (when declared) covers the rest of the file.</summary>
    private static long ProbeMatroska(CarveSource src)
    {
        Span<byte> b = stackalloc byte[12];
        if (!src.TryRead(4, b[..8]) || !TryReadVint(b[..8], out var vintLength, out var headerSize, out var unknown) || unknown || headerSize > 4096)
        {
            return No;
        }
        var segment = 4L + vintLength + (long)headerSize;
        if (!src.TryRead(segment, b) || !b[..4].SequenceEqual((ReadOnlySpan<byte>)[0x18, 0x53, 0x80, 0x67]))
        {
            return Unknown;                 // something other than a Segment follows; carve capped
        }
        if (!TryReadVint(b.Slice(4, 8), out vintLength, out var segmentSize, out unknown))
        {
            return Damaged;
        }
        if (unknown || segmentSize > long.MaxValue / 2)
        {
            return Unknown;                 // live-streamed files leave the size open
        }
        return segment + 4 + vintLength + (long)segmentSize;
    }

    private static bool TryReadVint(ReadOnlySpan<byte> s, out int length, out ulong value, out bool unknown)
    {
        length = 0;
        value = 0;
        unknown = false;
        if (s.Length == 0 || s[0] == 0)
        {
            return false;
        }
        length = System.Numerics.BitOperations.LeadingZeroCount((uint)s[0]) - 24 + 1;
        if (length > s.Length)
        {
            return false;
        }
        value = (ulong)(s[0] & (0xFF >> length));
        var allOnes = value == (ulong)(0xFF >> length);
        for (int i = 1; i < length; i++)
        {
            value = (value << 8) | s[i];
            allOnes &= s[i] == 0xFF;
        }
        unknown = allOnes;
        return true;
    }

    // ---- executables --------------------------------------------------------------------------

    /// <summary>
    /// PE image: DOS stub → "PE\0\0" → section table. The file ends after the last section's
    /// raw data, or after the certificate table when the image is signed.
    /// </summary>
    internal static long ProbePe(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 0x40)
        {
            return No;
        }
        var peOffset = U32(h, 0x3C);
        if (peOffset < 0x40 || peOffset > 0x10000)
        {
            return No;
        }
        Span<byte> coff = stackalloc byte[24];
        if (!src.TryRead(peOffset, coff) || !coff[..4].SequenceEqual("PE\0\0"u8))
        {
            return No;
        }
        var sections = U16(coff, 6);
        var optionalSize = U16(coff, 20);
        if (sections is 0 or > 96 || optionalSize is < 96 or > 4096)
        {
            return No;
        }

        var optional = new byte[optionalSize];
        if (!src.TryRead(peOffset + 24, optional))
        {
            return Damaged;
        }
        int directories;
        switch (U16(optional, 0))
        {
            case 0x10B: directories = 96; break;        // PE32
            case 0x20B: directories = 112; break;       // PE32+
            default: return No;
        }

        long end = U32(optional, 60);                   // SizeOfHeaders
        if (optionalSize >= directories + 5 * 8 && U32(optional, directories - 4) > 4)
        {
            // Directory 4 is the certificate table; unlike the others its address is a file offset.
            var certOffset = U32(optional, directories + 4 * 8);
            var certSize = U32(optional, directories + 4 * 8 + 4);
            if (certOffset != 0 && certSize != 0)
            {
                end = Math.Max(end, (long)certOffset + certSize);
            }
        }

        var table = new byte[sections * 40];
        if (!src.TryRead(peOffset + 24 + optionalSize, table))
        {
            return Damaged;
        }
        for (int i = 0; i < sections; i++)
        {
            var rawSize = U32(table, i * 40 + 16);
            var rawPointer = U32(table, i * 40 + 20);
            if (rawSize != 0 && rawPointer != 0)
            {
                end = Math.Max(end, (long)rawPointer + rawSize);
            }
        }
        return end > peOffset + 24 ? end : No;
    }

    private static long ProbeElf(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 0x34 || h[4] is not (1 or 2) || h[5] is not (1 or 2) || h[6] != 1)
        {
            return No;                      // class, data encoding, version
        }
        var is64 = h[4] == 2;
        var little = h[5] == 1;
        if (is64 && h.Length < 0x40)
        {
            return No;
        }

        ushort R16(ReadOnlySpan<byte> s, int o) => little ? U16(s, o) : U16Be(s, o);
        ulong R32(ReadOnlySpan<byte> s, int o) => little ? U32(s, o) : U32Be(s, o);
        ulong R64(ReadOnlySpan<byte> s, int o) => little ? U64(s, o) : U64Be(s, o);

        var type = R16(h, 0x10);
        if (type == 0 || (type > 4 && type < 0xFE00))
        {
            return No;
        }

        var programOffset = is64 ? R64(h, 0x20) : R32(h, 0x1C);
        var sectionOffset = is64 ? R64(h, 0x28) : R32(h, 0x20);
        var programEntry = R16(h, is64 ? 0x36 : 0x2A);
        var programCount = R16(h, is64 ? 0x38 : 0x2C);
        var sectionEntry = R16(h, is64 ? 0x3A : 0x2E);
        var sectionCount = R16(h, is64 ? 0x3C : 0x30);
        if (programOffset > long.MaxValue / 4 || sectionOffset > long.MaxValue / 4)
        {
            return No;
        }

        ulong end = 0;
        if (sectionOffset != 0 && sectionCount != 0)
        {
            end = Math.Max(end, sectionOffset + (ulong)sectionCount * sectionEntry);
        }
        if (programOffset != 0 && programCount != 0)
        {
            end = Math.Max(end, programOffset + (ulong)programCount * programEntry);
            if (programEntry == (is64 ? 56 : 32) && programCount <= 1024)
            {
                var table = new byte[programCount * programEntry];
                if (src.TryRead((long)programOffset, table))
                {
                    for (int i = 0; i < programCount; i++)
                    {
                        var e = table.AsSpan(i * programEntry, programEntry);
                        var offset = is64 ? R64(e, 8) : R32(e, 4);
                        var fileSize = is64 ? R64(e, 0x20) : R32(e, 16);
                        if (offset <= long.MaxValue / 4 && fileSize <= long.MaxValue / 4)
                        {
                            end = Math.Max(end, offset + fileSize);
                        }
                    }
                }
            }
        }
        return end > (ulong)(is64 ? 0x40 : 0x34) ? AsLength(end) : Unknown;
    }

    // ---- databases and Windows artifacts ------------------------------------------------------

    private static long ProbeSqlite(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 100)
        {
            return No;
        }
        int pageSize = U16Be(h, 16);
        if (pageSize == 1)
        {
            pageSize = 65536;
        }
        if (pageSize < 512 || (pageSize & (pageSize - 1)) != 0)
        {
            return No;
        }
        if (h[18] is not (1 or 2) || h[19] is not (1 or 2) || h[21] != 64 || h[22] != 32 || h[23] != 32)
        {
            return No;                      // file-format versions and the fixed payload fractions
        }
        var pages = U32Be(h, 28);
        // The in-header page count is only kept current by SQLite 3.7.0+, which marks it valid
        // by setting "version-valid-for" to the change counter.
        if (pages == 0 || U32Be(h, 24) != U32Be(h, 92))
        {
            return Unknown;
        }
        return (long)pageSize * pages;
    }

    private static long ProbePst(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 0xC0 || h[8] != 0x53 || h[9] != 0x4D)
        {
            return No;                      // wMagicClient "SM"
        }
        var version = U16(h, 10);
        ulong endOfFile;
        if (version is 14 or 15)
        {
            endOfFile = U32(h, 0xA8);       // ANSI: ibFileEof
        }
        else if (version >= 23)
        {
            endOfFile = U64(h, 0xB8);       // Unicode: ibFileEof
        }
        else
        {
            return No;
        }
        return endOfFile >= 0x200 ? AsLength(endOfFile) : No;
    }

    private static long ProbeRegf(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 0x200 || U32(h, 0x14) != 1)
        {
            return No;                      // major version
        }
        uint checksum = 0;
        for (int i = 0; i < 0x1FC; i += 4)
        {
            checksum ^= U32(h, i);
        }
        checksum = checksum switch { 0xFFFFFFFF => 0xFFFFFFFE, 0 => 1, _ => checksum };
        if (checksum != U32(h, 0x1FC))
        {
            return No;
        }
        var hiveBins = U32(h, 0x28);
        return hiveBins == 0 || (hiveBins & 0xFFF) != 0 ? Unknown : 4096L + hiveBins;
    }

    private static long ProbeEvtx(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 128 || U32(h, 0x20) != 128 || U16(h, 0x28) != 4096)
        {
            return No;                      // header size, header block size
        }
        var chunks = U16(h, 0x2A);
        return chunks == 0 ? Unknown : 4096L + chunks * 65536L;
    }

    // ---- network captures ---------------------------------------------------------------------

    /// <summary>Walks the packet records; the capture ends where they stop making sense.</summary>
    private static long ProbePcap(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 24 || U16(h, 4) != 2 || U16(h, 6) != 4)
        {
            return No;                      // version 2.4
        }
        var snapLength = U32(h, 16);
        if (snapLength is 0 or > 0x4000000 || (U32(h, 20) & 0x0FFFFFFF) > 1000)
        {
            return No;                      // link type
        }
        var maxPacket = Math.Max(snapLength, 65535u);

        Span<byte> record = stackalloc byte[16];
        long pos = 24;
        var packets = 0;
        while (packets < 50_000_000 && pos + 16 <= src.Limit && src.TryRead(pos, record))
        {
            var microseconds = U32(record, 4);
            var captured = U32(record, 8);
            var original = U32(record, 12);
            if (microseconds >= 1_000_000 || captured > maxPacket || captured > original || original > 0x4000000)
            {
                break;
            }
            pos += 16 + captured;
            packets++;
        }
        return packets > 0 ? pos : No;
    }

    private static long ProbePcapNg(CarveSource src)
    {
        var h = src.Head;
        if (h.Length < 28 || U32(h, 8) != 0x1A2B3C4D || U16(h, 12) != 1)
        {
            return No;                      // little-endian byte-order magic, major version
        }

        Span<byte> b = stackalloc byte[8];
        Span<byte> trailer = stackalloc byte[4];
        long pos = 0;
        var blocks = 0;
        while (blocks < 50_000_000 && pos + 12 <= src.Limit && src.TryRead(pos, b))
        {
            var length = U32(b, 4);
            if (length < 12 || (length & 3) != 0 || length > 64 * MiB)
            {
                break;
            }
            // Every block repeats its length at the end.
            if (!src.TryRead(pos + length - 4, trailer) || U32(trailer, 0) != length)
            {
                break;
            }
            pos += length;
            blocks++;
        }
        return blocks > 0 ? pos : No;
    }
}
