using System.Buffers.Binary;
using System.Text;
using DiscUtils.Ntfs;
using DiscUtils.Streams;

namespace Cinder.Filesystems;

/// <summary>One change-journal record: what happened to which file, when.</summary>
public sealed record UsnRecord(
    long Usn,
    DateTimeOffset Timestamp,
    string FileName,
    long MftIndex,
    int MftSequence,
    long ParentMftIndex,
    int ParentMftSequence,
    uint Reason,
    uint SourceInfo,
    uint FileAttributes,
    int Version)
{
    public string ReasonText => UsnJournal.DescribeReason(Reason);
    public bool IsDirectory => (FileAttributes & 0x10) != 0;
}

/// <summary>
/// Parser for the NTFS change journal (<c>$Extend\$UsnJrnl:$J</c>) — USN_RECORD_V2 and V3.
/// Every create, delete, rename, write and attribute change on the volume for as long as the
/// journal has been retained, with the file's MFT reference so the entry can be tied to an
/// $MFT record even after the file is gone. This is the single most-used NTFS artifact in
/// incident response, and the one the filesystem listing cannot substitute for.
///
/// <para>Input is attacker-controlled bytes, so record lengths are sanity-checked and the parser
/// resynchronises on anything implausible rather than trusting a length field into an
/// allocation. <c>$J</c> is sparse — its leading region is typically gigabytes of nothing — so
/// when the source is a <see cref="SparseStream"/> the allocated extents are read and the rest
/// skipped.</para>
/// </summary>
public static class UsnJournal
{
    private const int V2HeaderLength = 60;
    private const int V3HeaderLength = 76;
    private const int MinRecordLength = V2HeaderLength;
    private const int MaxRecordLength = 4096;

    /// <summary>Opens <c>$Extend\$UsnJrnl:$J</c> on an NTFS volume, or null if the volume has no journal.</summary>
#pragma warning disable CA1416 // read-only; see DiscUtilsWalker for the rationale
    public static Stream? Open(NtfsFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        try
        {
            // DiscUtils addresses named streams as path:stream:$TYPE.
            return fs.OpenFile(@"$Extend\$UsnJrnl:$J:$DATA", FileMode.Open, FileAccess.Read);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }
    }
#pragma warning restore CA1416

    /// <summary>Parses records from a <c>$J</c> stream (an extracted <c>$J</c> file works the same way).</summary>
    public static IEnumerable<UsnRecord> Parse(Stream journal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(journal);

        // One window buffer for the whole parse: a fragmented $J can have thousands of extents,
        // and a megabyte per extent is allocation the input should not be able to drive.
        var buffer = new byte[1 << 20];
        var seekable = journal.CanSeek;

        foreach (var (start, length) in DataRegions(journal))
        {
            // Saturating: a non-seekable source reports an unbounded region.
            var end = length > long.MaxValue - start ? long.MaxValue : start + length;
            var bufferStart = start;
            var have = 0;

            // True once nothing more can arrive for this region: its end was reached, or the
            // stream ran dry before the end it advertised (a truncated image does exactly
            // that). Every exit from the loop below depends on it; without it a cut-off
            // final record left the loop with nothing to consume and nothing to read.
            var exhausted = false;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Top the buffer up to a full window or the region end.
                if (!exhausted && have < MaxRecordLength)
                {
                    if (bufferStart + have >= end)
                    {
                        exhausted = true;
                    }
                    else
                    {
                        var want = (int)Math.Min(buffer.Length - have, end - (bufferStart + have));
                        if (seekable)
                        {
                            journal.Position = bufferStart + have;
                        }
                        var n = ReadFull(journal, buffer, have, want);
                        have += n;
                        if (n < want)
                        {
                            exhausted = true;
                        }
                    }
                }
                if (have == 0)
                {
                    break;
                }

                var consumed = 0;
                while (consumed + 8 <= have)
                {
                    var span = buffer.AsSpan(consumed, have - consumed);
                    var recordLength = BinaryPrimitives.ReadInt32LittleEndian(span);

                    if (recordLength == 0)
                    {
                        // Zero padding to the next page. Skip to the next 8-byte boundary and keep
                        // going; a long zero run just advances quickly.
                        consumed += 8;
                        continue;
                    }
                    if (recordLength < MinRecordLength || recordLength > MaxRecordLength || (recordLength & 7) != 0)
                    {
                        consumed += 8;   // implausible — resynchronise
                        continue;
                    }
                    if (recordLength > span.Length)
                    {
                        if (!exhausted)
                        {
                            break;       // need more bytes, and more can still be read
                        }

                        // The record claims bytes that will never arrive: a journal cut off
                        // mid-record, or a length field planted near the end. Treat it like
                        // any other implausible length and resynchronise, so whatever real
                        // records follow inside the claimed extent are still found.
                        consumed += 8;
                        continue;
                    }

                    if (TryParseRecord(span[..recordLength], out var record))
                    {
                        yield return record;
                    }
                    consumed += recordLength;
                }

                // Slide the unconsumed tail down and refill.
                var remaining = have - consumed;
                if (remaining > 0 && consumed > 0)
                {
                    System.Buffer.BlockCopy(buffer, consumed, buffer, 0, remaining);
                }
                bufferStart += consumed;
                have = remaining;

                // With nothing more to read, the scan above ran until fewer than 8 bytes were
                // left, so this is the end of the region. Otherwise the leftover is shorter
                // than one record (<= 4096 bytes) and the next pass must refill, which either
                // adds bytes or sets `exhausted` — the loop cannot repeat a state.
                if (exhausted)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Allocated extents of a sparse <c>$J</c>; the whole stream otherwise.</summary>
    private static IEnumerable<(long Start, long Length)> DataRegions(Stream s)
    {
        if (s is SparseStream sparse)
        {
            var any = false;
            foreach (var extent in sparse.Extents)
            {
                any = true;
                yield return (extent.Start, extent.Length);
            }
            if (any)
            {
                yield break;
            }
        }
        yield return (0, s.CanSeek ? s.Length : long.MaxValue);
    }

    internal static bool TryParseRecord(ReadOnlySpan<byte> r, out UsnRecord record)
    {
        record = null!;
        if (r.Length < 8)
        {
            return false;
        }
        var major = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
        int refSize;
        switch (major)
        {
            case 2: refSize = 8; break;
            case 3: refSize = 16; break;
            default: return false;   // V4 range-tracking records carry no name; unknown versions skipped
        }

        // The fixed part is 60 bytes for V2 and 76 for V3 (two 128-bit references). RecordLength
        // is only checked against the V2 minimum by the caller, so a record that claims V3 with
        // a length of 64 or 72 would otherwise be read past its end.
        if (r.Length < (refSize == 16 ? V3HeaderLength : V2HeaderLength))
        {
            return false;
        }

        var o = 8;
        long fileRef, parentRef;
        if (refSize == 8)
        {
            fileRef = BinaryPrimitives.ReadInt64LittleEndian(r[o..]); o += 8;
            parentRef = BinaryPrimitives.ReadInt64LittleEndian(r[o..]); o += 8;
        }
        else
        {
            fileRef = BinaryPrimitives.ReadInt64LittleEndian(r[o..]); o += 16;
            parentRef = BinaryPrimitives.ReadInt64LittleEndian(r[o..]); o += 16;
        }
        var usn = BinaryPrimitives.ReadInt64LittleEndian(r[o..]); o += 8;
        var filetime = BinaryPrimitives.ReadInt64LittleEndian(r[o..]); o += 8;
        var reason = BinaryPrimitives.ReadUInt32LittleEndian(r[o..]); o += 4;
        var sourceInfo = BinaryPrimitives.ReadUInt32LittleEndian(r[o..]); o += 4;
        o += 4;   // SecurityId
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(r[o..]); o += 4;
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(r[o..]); o += 2;
        var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(r[o..]);

        if (nameOffset < o + 2 || nameOffset + nameLength > r.Length || (nameLength & 1) != 0)
        {
            return false;
        }

        DateTimeOffset ts;
        try
        {
            ts = filetime <= 0 ? DateTimeOffset.MinValue : DateTimeOffset.FromFileTime(filetime).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var name = Encoding.Unicode.GetString(r.Slice(nameOffset, nameLength)).TrimEnd('\0');
        record = new UsnRecord(
            Usn: usn,
            Timestamp: ts,
            FileName: name,
            MftIndex: fileRef & 0xFFFF_FFFF_FFFFL,
            MftSequence: (int)((ulong)fileRef >> 48),
            ParentMftIndex: parentRef & 0xFFFF_FFFF_FFFFL,
            ParentMftSequence: (int)((ulong)parentRef >> 48),
            Reason: reason,
            SourceInfo: sourceInfo,
            FileAttributes: attributes,
            Version: major);
        return true;
    }

    private static readonly (uint Flag, string Name)[] ReasonFlags =
    [
        (0x00000001, "DATA_OVERWRITE"), (0x00000002, "DATA_EXTEND"), (0x00000004, "DATA_TRUNCATION"),
        (0x00000010, "NAMED_DATA_OVERWRITE"), (0x00000020, "NAMED_DATA_EXTEND"), (0x00000040, "NAMED_DATA_TRUNCATION"),
        (0x00000100, "FILE_CREATE"), (0x00000200, "FILE_DELETE"), (0x00000400, "EA_CHANGE"),
        (0x00000800, "SECURITY_CHANGE"), (0x00001000, "RENAME_OLD_NAME"), (0x00002000, "RENAME_NEW_NAME"),
        (0x00004000, "INDEXABLE_CHANGE"), (0x00008000, "BASIC_INFO_CHANGE"), (0x00010000, "HARD_LINK_CHANGE"),
        (0x00020000, "COMPRESSION_CHANGE"), (0x00040000, "ENCRYPTION_CHANGE"), (0x00080000, "OBJECT_ID_CHANGE"),
        (0x00100000, "REPARSE_POINT_CHANGE"), (0x00200000, "STREAM_CHANGE"), (0x00400000, "TRANSACTED_CHANGE"),
        (0x00800000, "INTEGRITY_CHANGE"), (0x80000000, "CLOSE"),
    ];

    /// <summary>Pipe-separated USN_REASON names, the way MFTECmd and Plaso print them.</summary>
    public static string DescribeReason(uint reason)
    {
        if (reason == 0)
        {
            return "";
        }
        var parts = new List<string>(4);
        foreach (var (flag, name) in ReasonFlags)
        {
            if ((reason & flag) != 0)
            {
                parts.Add(name);
            }
        }
        var known = ReasonFlags.Aggregate(0u, (acc, f) => acc | f.Flag);
        if ((reason & ~known) != 0)
        {
            parts.Add($"0x{reason & ~known:X}");
        }
        return string.Join("|", parts);
    }

    private static int ReadFull(Stream s, byte[] buffer, int offset, int count)
    {
        var filled = 0;
        while (filled < count)
        {
            var n = s.Read(buffer, offset + filled, count - filled);
            if (n <= 0) break;
            filled += n;
        }
        return filled;
    }
}
