using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Cinder.Imaging.Tests;

/// <summary>
/// Builds synthetic EWF (.E01) containers in memory so the reader can be tested against both
/// well-formed and deliberately hostile input without shipping binary fixtures.
///
/// <para>Layout produced (single segment):</para>
/// <code>
///   segment header (13)
///   [header2]   optional, zlib-compressed UTF-16 case metadata
///   volume      geometry
///   sectors     chunk payloads, back to back
///   table       one uint32 offset per chunk, relative to a base, bit 31 = compressed
///   digest      md5(16) + sha1(20) of the decoded media
///   done
/// </code>
/// </summary>
internal static class EwfBuilder
{
    private const int SegmentHeaderLength = 13;
    private const int SectionHeaderLength = 76;

    internal sealed record Options
    {
        internal uint BytesPerSector { get; init; } = 512;
        internal uint SectorsPerChunk { get; init; } = 8;      // 4 KiB chunks
        internal bool Compress { get; init; }
        internal bool IncludeDigest { get; init; } = true;
        internal bool IncludeHeader2 { get; init; } = true;

        /// <summary>Overrides the recorded MD5, to simulate a container whose hash doesn't match.</summary>
        internal byte[]? ForcedMd5 { get; init; }

        /// <summary>Corrupts the payload of this chunk index after the table is written.</summary>
        internal int? CorruptChunkIndex { get; init; }
    }

    /// <summary>Builds a well-formed container over <paramref name="media"/>.</summary>
    internal static byte[] Build(byte[] media, Options? options = null)
    {
        var o = options ?? new Options();
        var chunkSize = (int)(o.BytesPerSector * o.SectorsPerChunk);
        if (media.Length % o.BytesPerSector != 0)
        {
            throw new ArgumentException("Media length must be a whole number of sectors.", nameof(media));
        }

        var chunkCount = (media.Length + chunkSize - 1) / chunkSize;
        var sectorCount = media.Length / (int)o.BytesPerSector;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // ---- segment header ----
        w.Write(new byte[] { 0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00 });
        w.Write((byte)0x01);
        w.Write((ushort)1);
        w.Write((ushort)0);

        // ---- header2 (optional) ----
        if (o.IncludeHeader2)
        {
            var text = Encoding.Unicode.GetBytes("main\ndescription\nCinder synthetic test image\n");
            WriteSection(ms, "header2", Deflate(text));
        }

        // ---- volume ----
        var volume = new byte[1052];
        BitConverter.GetBytes((uint)chunkCount).CopyTo(volume, 4);
        BitConverter.GetBytes(o.SectorsPerChunk).CopyTo(volume, 8);
        BitConverter.GetBytes(o.BytesPerSector).CopyTo(volume, 12);
        BitConverter.GetBytes((uint)sectorCount).CopyTo(volume, 16);
        WriteSection(ms, "volume", volume);

        // ---- sectors: payloads back to back, remembering each chunk's offset ----
        var sectorsDataStart = ms.Position + SectionHeaderLength;
        var payloads = new List<byte[]>(chunkCount);
        var relativeOffsets = new List<uint>(chunkCount);
        var compressedFlags = new List<bool>(chunkCount);

        var running = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            var start = i * chunkSize;
            var len = Math.Min(chunkSize, media.Length - start);
            var plain = media.AsSpan(start, len).ToArray();

            byte[] payload;
            bool compressed;
            if (o.Compress)
            {
                payload = Deflate(plain);
                compressed = true;
            }
            else
            {
                payload = plain;
                compressed = false;
            }

            payloads.Add(payload);
            relativeOffsets.Add((uint)running);
            compressedFlags.Add(compressed);
            running += payload.Length;
        }

        var sectorsBlob = new byte[running];
        var cursor = 0;
        foreach (var p in payloads)
        {
            p.CopyTo(sectorsBlob, cursor);
            cursor += p.Length;
        }

        if (o.CorruptChunkIndex is { } bad && bad < payloads.Count)
        {
            // Scribble over the middle of that chunk's payload so inflate fails / mismatches.
            var at = (int)relativeOffsets[bad] + Math.Min(2, payloads[bad].Length - 1);
            var end = Math.Min(sectorsBlob.Length, at + Math.Max(1, payloads[bad].Length - 2));
            for (int i = at; i < end; i++)
            {
                sectorsBlob[i] ^= 0xFF;
            }
        }

        WriteSection(ms, "sectors", sectorsBlob);

        // ---- table ----
        var table = new byte[24 + chunkCount * 4];
        BitConverter.GetBytes((uint)chunkCount).CopyTo(table, 0);
        BitConverter.GetBytes((ulong)sectorsDataStart).CopyTo(table, 8);
        for (int i = 0; i < chunkCount; i++)
        {
            var v = relativeOffsets[i];
            if (compressedFlags[i])
            {
                v |= 0x8000_0000u;
            }
            BitConverter.GetBytes(v).CopyTo(table, 24 + i * 4);
        }
        WriteSection(ms, "table", table);

        // ---- digest ----
        if (o.IncludeDigest)
        {
            var digest = new byte[36];
            (o.ForcedMd5 ?? MD5.HashData(media)).CopyTo(digest, 0);
            SHA1.HashData(media).CopyTo(digest, 16);
            WriteSection(ms, "digest", digest);
        }

        WriteSection(ms, "done", [], isLast: true);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes a raw section with a caller-supplied descriptor, for malformed-input tests.
    /// <paramref name="declaredSize"/> and <paramref name="next"/> go into the descriptor
    /// verbatim, however implausible.
    /// </summary>
    internal static void WriteRawSection(MemoryStream ms, string type, byte[] data, long declaredSize, long next)
    {
        var header = new byte[SectionHeaderLength];
        Encoding.ASCII.GetBytes(type).CopyTo(header, 0);
        BitConverter.GetBytes(next).CopyTo(header, 16);
        BitConverter.GetBytes(declaredSize).CopyTo(header, 24);
        ms.Write(header);
        ms.Write(data);
    }

    /// <summary>Writes the 13-byte segment header that every EWF segment opens with.</summary>
    internal static void WriteSegmentHeader(MemoryStream ms)
    {
        ms.Write(new byte[] { 0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00 });
        ms.WriteByte(0x01);
        ms.Write(BitConverter.GetBytes((ushort)1));
        ms.Write(BitConverter.GetBytes((ushort)0));
    }

    private static void WriteSection(MemoryStream ms, string type, byte[] data, bool isLast = false)
    {
        var start = ms.Position;
        var size = SectionHeaderLength + data.Length;
        var next = isLast ? start : start + size;
        WriteRawSection(ms, type, data, size, next);
    }

    /// <summary>Raw-deflate wrapped in a zlib (RFC 1950) envelope, which is what EWF uses.</summary>
    internal static byte[] Deflate(byte[] input)
    {
        using var outMs = new MemoryStream();
        using (var z = new ZLibStream(outMs, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(input, 0, input.Length);
        }
        return outMs.ToArray();
    }

    /// <summary>Deterministic pseudo-random media so tests are reproducible.</summary>
    internal static byte[] Media(int bytes, int seed = 1234)
    {
        var rnd = new Random(seed);
        var b = new byte[bytes];
        rnd.NextBytes(b);
        return b;
    }
}
