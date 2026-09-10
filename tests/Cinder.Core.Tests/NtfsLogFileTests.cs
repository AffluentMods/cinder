using System.Buffers.Binary;
using System.Text;
using Cinder.Filesystems;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// Builds a synthetic <c>$LogFile</c> — restart page, fixup-protected RCRD pages, records that
/// carry FILE-record and index-entry payloads, one record spanning two pages, and stale slack
/// from an earlier cycle — and checks the parser recovers every field and every name.
/// </summary>
public sealed class NtfsLogFileTests
{
    private const int PageSize = 4096;
    private const int DataOffset = 0x40;
    private const int SeqBits = 44;
    private const ushort Usn = 0x1234;

    [Fact]
    public void Parses_records_names_transactions_and_multi_page_continuation()
    {
        var log = new LogBuilder();
        var t1 = 0x11u;

        // Page 2: a file record initialised (name from the FILE record), the entry added to
        // its parent's index, then a long record that spills into page 3.
        var lsnInit = log.Add(t1, redoOp: 0x02, undoOp: 0x03, redo: FileRecord(recordNo: 45, name: "invoice.pdf.exe", parent: 5), targetVcn: 11, clusterBlock: 2);
        var lsnAdd = log.Add(t1, redoOp: 0x0E, undoOp: 0x0F, redo: IndexEntry(childRecord: 45, name: "invoice.pdf.exe", parent: 5), targetVcn: 900, clusterBlock: 0);
        var lsnLong = log.Add(t1, redoOp: 0x08, undoOp: 0x08, redo: new byte[3000], targetVcn: 1234, clusterBlock: 0);

        // Page 3 (after the continuation tail): a delete whose undo payload carries the name.
        var lsnDel = log.Add(0x12, redoOp: 0x0F, undoOp: 0x0E, redo: [], undo: IndexEntry(childRecord: 45, name: "invoice.pdf.exe", parent: 5), targetVcn: 900, clusterBlock: 0);
        var lsnCommit = log.Add(0x12, redoOp: 0x1B, undoOp: 0x00, redo: [], targetVcn: 0, clusterBlock: 0);

        // Stale slack past next_record_offset on the last page, from an "earlier cycle".
        var bytes = log.Build(staleName: "secret-plan.docx");

        using var ms = new MemoryStream(bytes);
        var records = NtfsLogFile.Parse(ms, clusterSize: 4096).ToList();

        records.Should().HaveCountGreaterThanOrEqualTo(6);
        var init = records.Single(r => r.Lsn == lsnInit);
        init.RedoText.Should().Be("InitializeFileRecordSegment");
        init.UndoText.Should().Be("DeallocateFileRecordSegment");
        init.FileName.Should().Be("invoice.pdf.exe");
        init.ParentMftRecord.Should().Be(5);
        init.MftRecordNumber.Should().Be(45, "the FILE record's own number wins over the VCN estimate");
        init.TransactionId.Should().Be(t1);
        init.Created.Should().NotBeNull();
        init.IsStale.Should().BeFalse();

        var add = records.Single(r => r.Lsn == lsnAdd);
        add.RedoText.Should().Be("AddIndexEntryAllocation");
        add.FileName.Should().Be("invoice.pdf.exe");
        add.MftRecordNumber.Should().Be(45, "the index entry's file reference");

        var lng = records.Single(r => r.Lsn == lsnLong);
        lng.IsTruncated.Should().BeFalse("the record continues into the next page and is reassembled");
        lng.TargetVcn.Should().Be(1234);

        var del = records.Single(r => r.Lsn == lsnDel);
        del.RedoText.Should().Be("DeleteIndexEntryAllocation");
        del.FileName.Should().Be("invoice.pdf.exe", "the undo payload of a delete is the entry that was removed");

        records.Single(r => r.Lsn == lsnCommit).RedoText.Should().Be("ForgetTransaction");

        var stale = records.Where(r => r.IsStale).ToList();
        stale.Should().NotBeEmpty();
        stale.Should().Contain(r => r.FileName == "secret-plan.docx");

        records.Select(r => r.Lsn).Should().OnlyHaveUniqueItems("buffer pages mirror record pages and must not double-count");
    }

    [Fact]
    public void Torn_page_fixups_are_not_applied_and_garbage_does_not_throw()
    {
        var log = new LogBuilder();
        log.Add(1, 0x02, 0x03, FileRecord(7, "a.txt", 5), 0, 0);
        var bytes = log.Build();

        // Corrupt one sector's USN on page 2: the fixup check fails and the page is left alone.
        bytes[2 * PageSize + 512 - 2] ^= 0xFF;
        using var ms = new MemoryStream(bytes);
        var act = () => NtfsLogFile.Parse(ms).ToList();
        act.Should().NotThrow();

        // Random bytes are not a log file either.
        var junk = new byte[PageSize * 6];
        new Random(3).NextBytes(junk);
        using var ms2 = new MemoryStream(junk);
        NtfsLogFile.Parse(ms2).ToList().Should().BeEmpty();
    }

    [Fact]
    public void Operation_names_match_microsofts()
    {
        NtfsLogFile.DescribeOperation(0x02).Should().Be("InitializeFileRecordSegment");
        NtfsLogFile.DescribeOperation(0x0F).Should().Be("DeleteIndexEntryAllocation");
        NtfsLogFile.DescribeOperation(0x1B).Should().Be("ForgetTransaction");
        NtfsLogFile.DescribeOperation(0x77).Should().Be("0x77");
    }

    // ---- payload builders ----------------------------------------------------------------

    private static byte[] FileNameValue(string name, long parent)
    {
        var n = Encoding.Unicode.GetBytes(name);
        var v = new byte[0x42 + n.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(v, (ulong)parent | (5UL << 48));
        var now = DateTimeOffset.UtcNow.ToFileTime();
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(8 + i * 8), now);
        v[0x40] = (byte)name.Length;
        v[0x41] = 3;
        n.CopyTo(v, 0x42);
        return v;
    }

    private static byte[] FileRecord(long recordNo, string name, long parent)
    {
        var fn = FileNameValue(name, parent);
        var attrLen = (0x18 + fn.Length + 7) & ~7;
        var r = new byte[0x38 + attrLen + 8];
        "FILE"u8.CopyTo(r);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x14), 0x38);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x2C), (uint)recordNo);
        var a = r.AsSpan(0x38);
        BinaryPrimitives.WriteUInt32LittleEndian(a, 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(a[4..], (uint)attrLen);
        a[8] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(a[0x10..], (uint)fn.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(a[0x14..], 0x18);
        fn.CopyTo(a[0x18..]);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x38 + attrLen), 0xFFFFFFFF);
        return r;
    }

    private static byte[] IndexEntry(long childRecord, string name, long parent)
    {
        var fn = FileNameValue(name, parent);
        var e = new byte[0x10 + fn.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(e, (ulong)childRecord | (2UL << 48));
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(8), (ushort)e.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(10), (ushort)fn.Length);
        fn.CopyTo(e, 0x10);
        return e;
    }

    /// <summary>Lays records out page by page exactly as NTFS does, including continuation and fixups.</summary>
    private sealed class LogBuilder
    {
        private readonly List<byte[]> _pages = [];
        private byte[] _page = NewPage();
        private int _pos = DataOffset;
        private long _prevLsn;

        private static byte[] NewPage()
        {
            var p = new byte[PageSize];
            "RCRD"u8.CopyTo(p);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), 0x28);   // USA offset
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), 9);      // USA count: 1 + 8 sectors
            return p;
        }

        private long CurrentFileOffset => (2 + _pages.Count) * (long)PageSize + _pos;
        private static long LsnFor(long fileOffset) => (fileOffset / 8) | (1L << SeqBits);

        public long Add(uint txn, ushort redoOp, ushort undoOp, byte[] redo, long targetVcn, ushort clusterBlock, byte[]? undo = null)
        {
            undo ??= [];
            var body = new byte[0x20 + Pad8(redo.Length) + Pad8(undo.Length)];
            BinaryPrimitives.WriteUInt16LittleEndian(body, redoOp);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), undoOp);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 0x20);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), (ushort)redo.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), (ushort)(0x20 + Pad8(redo.Length)));
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(10), (ushort)undo.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(20), clusterBlock);
            BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(24), targetVcn);
            redo.CopyTo(body, 0x20);
            undo.CopyTo(body, 0x20 + Pad8(redo.Length));

            if (_pos + 0x30 > PageSize)
            {
                Flush();
            }
            var lsn = LsnFor(CurrentFileOffset);
            var header = new byte[0x30];
            BinaryPrimitives.WriteInt64LittleEndian(header, lsn);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), _prevLsn);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18), (uint)body.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x20), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x24), txn);
            header.CopyTo(_page, _pos);
            _pos += 0x30;

            var written = 0;
            while (written < body.Length)
            {
                var room = PageSize - _pos;
                if (room == 0)
                {
                    Flush();
                    room = PageSize - _pos;
                }
                var take = Math.Min(room, body.Length - written);
                body.AsSpan(written, take).CopyTo(_page.AsSpan(_pos));
                _pos += take;
                written += take;
            }
            _pos = (_pos + 7) & ~7;
            BinaryPrimitives.WriteUInt16LittleEndian(_page.AsSpan(0x18), (ushort)Math.Min(_pos, PageSize));
            BinaryPrimitives.WriteInt64LittleEndian(_page.AsSpan(0x20), lsn);   // last_end_lsn
            _prevLsn = lsn;
            return lsn;
        }

        private void Flush()
        {
            _pages.Add(_page);
            _page = NewPage();
            _pos = DataOffset;
        }

        public byte[] Build(string? staleName = null)
        {
            if (staleName is not null)
            {
                // A record from an earlier cycle sitting right after the live data, past
                // next_record_offset — exactly where circular-log slack lives.
                var entry = IndexEntry(childRecord: 99, name: staleName, parent: 5);
                var body = new byte[0x20 + entry.Length];
                if (_pos + 0x30 + body.Length > PageSize)
                {
                    Flush();
                    BinaryPrimitives.WriteUInt16LittleEndian(_page.AsSpan(0x18), DataOffset);
                }
                var stalePos = _pos;
                BinaryPrimitives.WriteUInt16LittleEndian(body, 0x0E);
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), 0x0F);
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 0x20);
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), (ushort)entry.Length);
                entry.CopyTo(body, 0x20);
                var header = new byte[0x30];
                BinaryPrimitives.WriteInt64LittleEndian(header, LsnFor(1_000_000));   // wrong offset: an old cycle
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18), (uint)body.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x20), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x24), 0x0A);
                header.CopyTo(_page, stalePos);
                body.CopyTo(_page, stalePos + 0x30);
            }
            _pages.Add(_page);

            var restart = new byte[PageSize];
            "RSTR"u8.CopyTo(restart);
            BinaryPrimitives.WriteUInt16LittleEndian(restart.AsSpan(4), 0x1E);
            BinaryPrimitives.WriteUInt16LittleEndian(restart.AsSpan(6), 9);
            BinaryPrimitives.WriteUInt32LittleEndian(restart.AsSpan(0x10), PageSize);
            BinaryPrimitives.WriteUInt32LittleEndian(restart.AsSpan(0x14), PageSize);
            BinaryPrimitives.WriteUInt16LittleEndian(restart.AsSpan(0x18), 0x30);
            BinaryPrimitives.WriteUInt32LittleEndian(restart.AsSpan(0x30 + 0x0C), SeqBits);
            BinaryPrimitives.WriteUInt16LittleEndian(restart.AsSpan(0x30 + 0x1A), DataOffset);
            Fixup(restart, 0x1E);

            var all = new List<byte[]> { restart, (byte[])restart.Clone() };
            foreach (var p in _pages)
            {
                Fixup(p, 0x28);
                all.Add(p);
            }
            // Pages 2 and 3 in a real log are buffer copies of the newest pages; mimic that
            // by inserting duplicates of the first two record pages ahead of the run.
            all.Insert(2, (byte[])all[2].Clone());
            if (_pages.Count > 1) all.Insert(3, (byte[])all[4].Clone());

            var result = new byte[all.Count * PageSize];
            for (int i = 0; i < all.Count; i++) all[i].CopyTo(result, i * PageSize);
            return result;
        }

        private static void Fixup(byte[] page, int usaOfs)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(usaOfs), Usn);
            for (int i = 1; i <= 8; i++)
            {
                var end = i * 512 - 2;
                page.AsSpan(end, 2).CopyTo(page.AsSpan(usaOfs + i * 2));
                BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(end), Usn);
            }
        }

        private static int Pad8(int n) => (n + 7) & ~7;
    }
}
