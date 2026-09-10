using System.Buffers.Binary;
using System.Text;
using DiscUtils.Ntfs;

namespace Cinder.Filesystems;

/// <summary>One NTFS transaction-log record: an operation against an MFT record or index.</summary>
public sealed record LogFileRecord(
    long Lsn,
    long PreviousLsn,
    long UndoNextLsn,
    uint TransactionId,
    int RecordType,
    ushort RedoOp,
    ushort UndoOp,
    long TargetVcn,
    long? TargetLcn,
    ushort ClusterBlockOffset,
    ushort RecordOffset,
    ushort AttributeOffset,
    ushort TargetAttribute,
    long? MftRecordNumber,
    string? FileName,
    long? ParentMftRecord,
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    bool IsStale,
    bool IsTruncated,
    long FileOffset)
{
    public string RedoText => NtfsLogFile.DescribeOperation(RedoOp);
    public string UndoText => NtfsLogFile.DescribeOperation(UndoOp);
    public bool IsCheckpoint => RecordType == 2;
}

/// <summary>
/// Parser for <c>$LogFile</c>, the NTFS transaction journal: every metadata change the
/// filesystem made — file records initialised and deallocated, index entries added and
/// removed, attributes created, resized and deleted — as redo / undo pairs grouped by
/// transaction. It is the third NTFS journal after <c>$MFT</c> and <c>$UsnJrnl</c>, and the
/// one that still holds a deleted file's name and parent after both of those have moved on,
/// because the index-entry and file-record payloads carry a full <c>$FILE_NAME</c>.
///
/// <para>Layout: two restart pages (<c>RSTR</c>), two buffer pages that mirror the most
/// recently written pages, then a circular run of 4 KiB record pages (<c>RCRD</c>). Every
/// page has update-sequence fixups. Records are 48-byte headers followed by client data;
/// a record can continue across page boundaries. Bytes past a page's
/// <c>next_record_offset</c> are slack from earlier cycles of the circular log — parsed
/// too, flagged <see cref="LogFileRecord.IsStale"/>, because that slack is often the only
/// place a long-deleted name survives.</para>
///
/// <para>Names are recovered opportunistically from the payloads that carry a
/// <c>$FILE_NAME</c> structure: file-record initialisation, index-entry add / delete, and
/// attribute create / delete. Everything is bounds-checked; the input is evidence and
/// may be corrupt or hostile.</para>
/// </summary>
public static class NtfsLogFile
{
    private const int DefaultPageSize = 4096;
    private const int RecordHeaderLength = 0x30;
    private const int MaxClientDataLength = 1 << 20;

    /// <summary>Opens <c>$LogFile</c> on an NTFS volume (null if absent) and reports the volume's cluster size.</summary>
#pragma warning disable CA1416 // read-only; see DiscUtilsWalker for the rationale
    public static Stream? Open(NtfsFileSystem fs, out int clusterSize)
    {
        ArgumentNullException.ThrowIfNull(fs);
        clusterSize = (int)fs.ClusterSize;
        try
        {
            return fs.OpenFile("$LogFile", FileMode.Open, FileAccess.Read);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }
    }
#pragma warning restore CA1416

    /// <summary>
    /// Parses every record page. <paramref name="clusterSize"/> is only used to turn a
    /// target VCN + cluster-block offset into an MFT record number for operations that
    /// address the MFT; 4096 is right for almost every volume.
    /// </summary>
    public static IEnumerable<LogFileRecord> Parse(Stream logFile, int clusterSize = 4096, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(logFile);
        if (!logFile.CanSeek)
        {
            throw new ArgumentException("$LogFile parsing needs a seekable stream.", nameof(logFile));
        }

        var length = logFile.Length;
        var pageSize = DefaultPageSize;
        var dataOffset = 0x40;
        var seqBits = 0;

        // Restart page: page size, log-page data offset, sequence-number bits.
        var first = ReadPage(logFile, 0, pageSize);
        if (first is not null && first.AsSpan(0, 4).SequenceEqual("RSTR"u8))
        {
            var logPageSize = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(0x14));
            if (logPageSize is >= 512 and <= 65536 && (logPageSize & (logPageSize - 1)) == 0)
            {
                pageSize = (int)logPageSize;
            }
            var raOffset = BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(0x18));
            if (raOffset + 0x30 <= first.Length)
            {
                seqBits = (int)BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(raOffset + 0x0C));
                var dOff = BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(raOffset + 0x1A));
                if (dOff >= 0x28 && dOff < pageSize)
                {
                    dataOffset = dOff;
                }
            }
        }

        var seen = new HashSet<long>();
        var pageCount = length / pageSize;
        var page = new byte[pageSize];
        var payloadPerPage = pageSize - dataOffset;

        for (long p = 2; p < pageCount; p++)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryReadPage(logFile, p * pageSize, page))
            {
                yield break;
            }
            if (!page.AsSpan(0, 4).SequenceEqual("RCRD"u8))
            {
                continue;
            }
            ApplyFixups(page);
            var nextRecordOffset = NextRecordOffset(page, dataOffset, pageSize);

            var pos = dataOffset;
            while (pos + RecordHeaderLength <= pageSize)
            {
                ct.ThrowIfCancellationRequested();
                var stale = pos >= nextRecordOffset;
                var lsn = BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(pos));
                if (lsn <= 0)
                {
                    break;
                }
                var dataLen = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(pos + 0x18)));
                var recordType = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(pos + 0x20));
                if (dataLen > MaxClientDataLength || recordType is not (1 or 2))
                {
                    break;   // not a record — the rest of the page is noise
                }

                // A record's LSN encodes its own file offset; a mismatch means we are reading
                // slack that has been partially overwritten — still worth a row, but one whose
                // header we no longer trust enough to follow across pages.
                var recordFileOffset = p * pageSize + pos;
                if (seqBits > 0 && seqBits < 64 && (long)(((ulong)lsn << seqBits) >> seqBits) * 8 != recordFileOffset)
                {
                    stale = true;
                }

                var header = page.AsSpan(pos, RecordHeaderLength).ToArray();
                var available = pageSize - (pos + RecordHeaderLength);

                if (dataLen <= available)
                {
                    var data = page.AsSpan(pos + RecordHeaderLength, dataLen).ToArray();
                    if (seen.Add(lsn))
                    {
                        yield return Build(header, data, lsn, recordType, clusterSize, stale, truncated: false, recordFileOffset);
                    }
                    pos = Align8(pos + RecordHeaderLength + dataLen);
                    continue;
                }

                if (stale)
                {
                    // Slack that runs off the page: what follows belongs to a newer cycle.
                    if (seen.Add(lsn))
                    {
                        yield return Build(header, page.AsSpan(pos + RecordHeaderLength, available).ToArray(), lsn, recordType, clusterSize, stale, truncated: true, recordFileOffset);
                    }
                    break;
                }

                // Live record continuing into the following page(s): the tail of the client
                // data sits at each next page's data offset.
                var full = new byte[dataLen];
                page.AsSpan(pos + RecordHeaderLength, available).CopyTo(full);
                var filled = available;
                var q = p + 1;
                var lastTake = 0;
                var cont = new byte[pageSize];
                while (filled < dataLen && q < pageCount)
                {
                    if (!TryReadPage(logFile, q * pageSize, cont) || !cont.AsSpan(0, 4).SequenceEqual("RCRD"u8))
                    {
                        break;
                    }
                    ApplyFixups(cont);
                    lastTake = Math.Min(payloadPerPage, dataLen - filled);
                    cont.AsSpan(dataOffset, lastTake).CopyTo(full.AsSpan(filled));
                    filled += lastTake;
                    q++;
                }

                if (filled < dataLen)
                {
                    if (seen.Add(lsn))
                    {
                        yield return Build(header, full[..filled], lsn, recordType, clusterSize, stale, truncated: true, recordFileOffset);
                    }
                    break;
                }

                if (seen.Add(lsn))
                {
                    yield return Build(header, full, lsn, recordType, clusterSize, stale, truncated: false, recordFileOffset);
                }

                // Resume inside the last continuation page, after this record's tail.
                p = q - 1;
                cont.CopyTo(page, 0);
                nextRecordOffset = NextRecordOffset(page, dataOffset, pageSize);
                pos = lastTake == payloadPerPage ? pageSize : Align8(dataOffset + lastTake);
            }
        }
    }

    private static int NextRecordOffset(byte[] page, int dataOffset, int pageSize)
    {
        int next = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(0x18));
        return next < dataOffset || next > pageSize ? pageSize : next;
    }

    private static int Align8(int v) => (v + 7) & ~7;

    private static byte[]? ReadPage(Stream s, long offset, int size)
    {
        var b = new byte[size];
        return TryReadPage(s, offset, b) ? b : null;
    }

    private static bool TryReadPage(Stream s, long offset, byte[] page)
    {
        if (offset + page.Length > s.Length)
        {
            return false;
        }
        s.Position = offset;
        var filled = 0;
        while (filled < page.Length)
        {
            var n = s.Read(page, filled, page.Length - filled);
            if (n <= 0)
            {
                return false;
            }
            filled += n;
        }
        return true;
    }

    /// <summary>Undoes the update-sequence protection: the last two bytes of every 512-byte sector hold the USN; the originals live in the array.</summary>
    internal static bool ApplyFixups(Span<byte> page)
    {
        var usaOfs = BinaryPrimitives.ReadUInt16LittleEndian(page[4..]);
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(page[6..]);
        if (usaCount < 2 || usaOfs + usaCount * 2 > page.Length || (usaCount - 1) * 512 > page.Length)
        {
            return false;
        }
        var usn = page.Slice(usaOfs, 2);
        for (int i = 1; i < usaCount; i++)
        {
            var sectorEnd = i * 512 - 2;
            if (!page.Slice(sectorEnd, 2).SequenceEqual(usn))
            {
                return false;   // torn write or not a real page — leave it
            }
            page.Slice(usaOfs + i * 2, 2).CopyTo(page.Slice(sectorEnd, 2));
        }
        return true;
    }

    private static LogFileRecord Build(byte[] header, byte[] data, long lsn, uint recordType, int clusterSize,
        bool stale, bool truncated, long fileOffset)
    {
        var prevLsn = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x08));
        var undoNext = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x10));
        var txn = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x24));

        if (recordType == 2 || data.Length < 0x20)
        {
            return new LogFileRecord(lsn, prevLsn, undoNext, txn, (int)recordType, 0, 0, 0, null, 0, 0, 0, 0,
                null, null, null, null, null, stale, truncated, fileOffset);
        }

        var d = data.AsSpan();
        var redoOp = BinaryPrimitives.ReadUInt16LittleEndian(d);
        var undoOp = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
        var redoOff = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]);
        var redoLen = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]);
        var undoOff = BinaryPrimitives.ReadUInt16LittleEndian(d[8..]);
        var undoLen = BinaryPrimitives.ReadUInt16LittleEndian(d[10..]);
        var targetAttr = BinaryPrimitives.ReadUInt16LittleEndian(d[12..]);
        var lcnsToFollow = BinaryPrimitives.ReadUInt16LittleEndian(d[14..]);
        var recordOffset = BinaryPrimitives.ReadUInt16LittleEndian(d[16..]);
        var attrOffset = BinaryPrimitives.ReadUInt16LittleEndian(d[18..]);
        var clusterBlock = BinaryPrimitives.ReadUInt16LittleEndian(d[20..]);
        var targetVcn = BinaryPrimitives.ReadInt64LittleEndian(d[24..]);
        long? targetLcn = lcnsToFollow > 0 && d.Length >= 0x28 ? BinaryPrimitives.ReadInt64LittleEndian(d[32..]) : null;

        long? mftRecord = null;
        if (IsMftOperation(redoOp) || IsMftOperation(undoOp))
        {
            // MFT record = (VCN × cluster + block × 512) / 1024. Correct whenever the target
            // is the MFT, which these operations always address.
            var bytes = targetVcn * clusterSize + (long)clusterBlock * 512;
            if (bytes >= 0)
            {
                mftRecord = bytes / 1024;
            }
        }

        string? name = null;
        long? parent = null;
        DateTimeOffset? created = null, modified = null;

        var redo = Slice(d, redoOff, redoLen);
        var undo = Slice(d, undoOff, undoLen);
        switch (redoOp)
        {
            case 0x02: // InitializeFileRecordSegment — payload is a FILE record
                (name, parent, created, modified, var recNo) = FromFileRecord(redo);
                if (recNo is not null) mftRecord = recNo;
                break;
            case 0x0C: // AddIndexEntryRoot
            case 0x0E: // AddIndexEntryAllocation
                (name, parent, created, modified) = FromIndexEntry(redo, out var childRef);
                mftRecord ??= childRef;
                break;
            case 0x05: // CreateAttribute
                (name, parent, created, modified) = FromAttribute(redo);
                break;
        }
        if (name is null)
        {
            switch (undoOp)
            {
                case 0x0C:
                case 0x0E: // undo of a delete is an add: the entry being removed
                    (name, parent, created, modified) = FromIndexEntry(undo, out var childRef);
                    mftRecord ??= childRef;
                    break;
                case 0x05: // undo of DeleteAttribute is CreateAttribute: the attribute removed
                    (name, parent, created, modified) = FromAttribute(undo);
                    break;
            }
        }
        if (name is null && (redoOp is 0x0D or 0x0F) && undo.Length == 0 && redo.Length >= 0x52)
        {
            // Some writers put the deleted entry in the redo payload.
            (name, parent, created, modified) = FromIndexEntry(redo, out _);
        }

        return new LogFileRecord(lsn, prevLsn, undoNext, txn, (int)recordType, redoOp, undoOp, targetVcn, targetLcn,
            clusterBlock, recordOffset, attrOffset, targetAttr, mftRecord, name, parent, created, modified, stale, truncated, fileOffset);
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> d, int off, int len) =>
        off >= 0 && len > 0 && off + len <= d.Length ? d.Slice(off, len) : ReadOnlySpan<byte>.Empty;

    private static bool IsMftOperation(ushort op) => op is 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x07 or 0x09 or 0x0B or 0x0C or 0x0D or 0x11 or 0x13 or 0x21 or 0x23 or 0x25;

    // ---- payload decoders ----------------------------------------------------------------

    /// <summary>A FILE record: walk its attributes for the first $FILE_NAME.</summary>
    private static (string? Name, long? Parent, DateTimeOffset? Created, DateTimeOffset? Modified, long? RecordNumber) FromFileRecord(ReadOnlySpan<byte> r)
    {
        if (r.Length < 0x30 || !r[..4].SequenceEqual("FILE"u8))
        {
            return (null, null, null, null, null);
        }
        long? recNo = BinaryPrimitives.ReadUInt32LittleEndian(r[0x2C..]);
        var attrOff = BinaryPrimitives.ReadUInt16LittleEndian(r[0x14..]);
        var pos = (int)attrOff;
        for (int guard = 0; guard < 64 && pos + 0x18 <= r.Length; guard++)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(r[pos..]);
            if (type == 0xFFFFFFFF)
            {
                break;
            }
            var len = BinaryPrimitives.ReadUInt32LittleEndian(r[(pos + 4)..]);
            if (len < 0x18 || pos + len > r.Length)
            {
                break;
            }
            var nonResident = r[pos + 8] != 0;
            if (type == 0x30 && !nonResident)
            {
                var valueLen = BinaryPrimitives.ReadUInt32LittleEndian(r[(pos + 0x10)..]);
                var valueOff = BinaryPrimitives.ReadUInt16LittleEndian(r[(pos + 0x14)..]);
                if (valueOff + valueLen <= len)
                {
                    var (n, p, c, m) = FromFileNameValue(r.Slice(pos + valueOff, (int)valueLen));
                    if (n is not null)
                    {
                        return (n, p, c, m, recNo);
                    }
                }
            }
            pos += (int)len;
        }
        return (null, null, null, null, recNo);
    }

    /// <summary>An index entry: file reference, lengths, then a $FILE_NAME key.</summary>
    private static (string? Name, long? Parent, DateTimeOffset? Created, DateTimeOffset? Modified) FromIndexEntry(ReadOnlySpan<byte> e, out long? childRecord)
    {
        childRecord = null;
        if (e.Length < 0x10 + 0x42)
        {
            return (null, null, null, null);
        }
        var fileRef = BinaryPrimitives.ReadUInt64LittleEndian(e);
        var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]);
        if (keyLen < 0x42 || 0x10 + keyLen > e.Length)
        {
            return (null, null, null, null);
        }
        childRecord = (long)(fileRef & 0x0000FFFFFFFFFFFF);
        return FromFileNameValue(e.Slice(0x10, keyLen));
    }

    /// <summary>An attribute record; only $FILE_NAME (0x30) carries a name.</summary>
    private static (string? Name, long? Parent, DateTimeOffset? Created, DateTimeOffset? Modified) FromAttribute(ReadOnlySpan<byte> a)
    {
        if (a.Length < 0x18)
        {
            return (null, null, null, null);
        }
        var type = BinaryPrimitives.ReadUInt32LittleEndian(a);
        if (type != 0x30 || a[8] != 0)
        {
            return (null, null, null, null);
        }
        var valueLen = BinaryPrimitives.ReadUInt32LittleEndian(a[0x10..]);
        var valueOff = BinaryPrimitives.ReadUInt16LittleEndian(a[0x14..]);
        if (valueOff + valueLen > a.Length)
        {
            return (null, null, null, null);
        }
        return FromFileNameValue(a.Slice(valueOff, (int)valueLen));
    }

    private static (string? Name, long? Parent, DateTimeOffset? Created, DateTimeOffset? Modified) FromFileNameValue(ReadOnlySpan<byte> v)
    {
        if (v.Length < 0x42)
        {
            return (null, null, null, null);
        }
        var parentRef = BinaryPrimitives.ReadUInt64LittleEndian(v);
        var created = FileTime(BinaryPrimitives.ReadInt64LittleEndian(v[8..]));
        var modified = FileTime(BinaryPrimitives.ReadInt64LittleEndian(v[16..]));
        int nameLen = v[0x40];
        if (nameLen == 0 || 0x42 + nameLen * 2 > v.Length)
        {
            return (null, null, null, null);
        }
        var name = Encoding.Unicode.GetString(v.Slice(0x42, nameLen * 2));
        foreach (var ch in name)
        {
            if (ch == '\0' || (ch < 0x20 && ch != '\t'))
            {
                return (null, null, null, null);   // not text — a payload that only looked like $FILE_NAME
            }
        }
        return (name, (long)(parentRef & 0x0000FFFFFFFFFFFF), created, modified);
    }

    private static DateTimeOffset? FileTime(long ft)
    {
        if (ft <= 0 || ft > 0x7FFF_FFFF_FFFF_FFFF - 1)
        {
            return null;
        }
        try
        {
            var t = DateTimeOffset.FromFileTime(ft);
            return t.Year is >= 1980 and <= 2200 ? t : null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ---- operation names --------------------------------------------------------------------

    public static string DescribeOperation(ushort op) => op switch
    {
        0x00 => "Noop",
        0x01 => "CompensationLogRecord",
        0x02 => "InitializeFileRecordSegment",
        0x03 => "DeallocateFileRecordSegment",
        0x04 => "WriteEndOfFileRecordSegment",
        0x05 => "CreateAttribute",
        0x06 => "DeleteAttribute",
        0x07 => "UpdateResidentValue",
        0x08 => "UpdateNonresidentValue",
        0x09 => "UpdateMappingPairs",
        0x0A => "DeleteDirtyClusters",
        0x0B => "SetNewAttributeSizes",
        0x0C => "AddIndexEntryRoot",
        0x0D => "DeleteIndexEntryRoot",
        0x0E => "AddIndexEntryAllocation",
        0x0F => "DeleteIndexEntryAllocation",
        0x10 => "WriteEndOfIndexBuffer",
        0x11 => "SetIndexEntryVcnRoot",
        0x12 => "SetIndexEntryVcnAllocation",
        0x13 => "UpdateFileNameRoot",
        0x14 => "UpdateFileNameAllocation",
        0x15 => "SetBitsInNonresidentBitMap",
        0x16 => "ClearBitsInNonresidentBitMap",
        0x17 => "HotFix",
        0x18 => "EndTopLevelAction",
        0x19 => "PrepareTransaction",
        0x1A => "CommitTransaction",
        0x1B => "ForgetTransaction",
        0x1C => "OpenNonresidentAttribute",
        0x1D => "OpenAttributeTableDump",
        0x1E => "AttributeNamesDump",
        0x1F => "DirtyPageTableDump",
        0x20 => "TransactionTableDump",
        0x21 => "UpdateRecordDataRoot",
        0x22 => "UpdateRecordDataAllocation",
        0x23 => "UpdateRelativeDataIndex",
        0x24 => "UpdateRelativeDataAllocation",
        0x25 => "ZeroEndOfFileRecord",
        _ => $"0x{op:X2}",
    };
}
