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
    private const int HomePage0 = 4;                 // restart, restart, tail copy, tail copy, then the circular area
    private const long FileSizeField = 64L << 20;    // the default $LogFile size
    private const int SeqBits = 41;                  // 67 - log2(64 MiB): what NTFS writes for that size
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
        records.Where(r => r.Lsn == lsnInit || r.Lsn == lsnAdd || r.Lsn == lsnLong || r.Lsn == lsnDel || r.Lsn == lsnCommit)
            .Should().OnlyContain(r => !r.IsStale, "live records are not slack, wherever they were read from");
    }

    [Fact]
    public void Restart_area_fields_are_read_at_their_on_disk_offsets()
    {
        // The restart area the builder writes follows the documented RESTART_AREA layout with a
        // 64 MiB file_size. The parser used to read the data offset from +0x1A, which is the
        // middle of file_size: 64 MiB reads there as 0x400, so every page was walked from
        // 0x400 and a default-sized log parsed to nothing.
        var log = new LogBuilder();
        var lsn = log.Add(0x21, redoOp: 0x02, undoOp: 0x03, redo: FileRecord(77, "first.txt", 5), targetVcn: 3, clusterBlock: 0);
        var bytes = log.Build();

        var restartArea = bytes.AsSpan(0x30);
        BinaryPrimitives.ReadInt64LittleEndian(restartArea[0x18..]).Should().Be(64L << 20, "the fixture must exercise the default log size");
        BinaryPrimitives.ReadUInt16LittleEndian(restartArea[0x1A..]).Should().Be(0x400, "that is what the old offset read as the data offset");

        using var ms = new MemoryStream(bytes);
        var records = NtfsLogFile.Parse(ms).ToList();

        var r = records.Should().ContainSingle().Subject;
        r.Lsn.Should().Be(lsn);
        r.FileName.Should().Be("first.txt");
        r.IsStale.Should().BeFalse();
    }

    [Fact]
    public void A_record_whose_lsn_does_not_encode_its_position_is_flagged_stale()
    {
        // Proves the sequence-number bits are really read: with them the parser can tell that
        // a header's LSN points somewhere else. It read them from +0x0C before (two unrelated
        // 16-bit fields), got 0 or 0x20000, and silently skipped this check on every log.
        var log = new LogBuilder();
        var good = log.Add(1, 0x02, 0x03, FileRecord(7, "a.txt", 5), 0, 0);
        var elsewhere = LogBuilder.LsnFor(HomePage0 * (long)PageSize + 0x800);
        var bad = log.Add(1, 0x02, 0x03, FileRecord(8, "b.txt", 5), 0, 0, lsnOverride: elsewhere);

        using var ms = new MemoryStream(log.Build());
        var records = NtfsLogFile.Parse(ms).ToList();

        records.Single(r => r.Lsn == good).IsStale.Should().BeFalse();
        records.Single(r => r.Lsn == bad).IsStale.Should().BeTrue();
    }

    [Fact]
    public void Newest_records_that_exist_only_in_a_tail_copy_page_are_reported_and_not_stale()
    {
        var log = new LogBuilder();
        var older = log.Add(1, 0x02, 0x03, FileRecord(7, "older.txt", 5), 0, 0);
        var newest = log.Add(1, 0x0E, 0x0F, IndexEntry(9, "newest.txt", 5), 900, 0);
        var bytes = log.Build();

        // The home page has not been written yet — only the tail copy at page 2 holds these
        // records, at an LSN that names the home position.
        bytes.AsSpan(HomePage0 * PageSize, PageSize).Clear();

        using var ms = new MemoryStream(bytes);
        var records = NtfsLogFile.Parse(ms).ToList();

        records.Select(r => r.Lsn).Should().BeEquivalentTo([older, newest]);
        records.Should().OnlyContain(r => !r.IsStale, "a tail copy's LSNs point at its home page, which is not a mismatch");
        records.Single(r => r.Lsn == newest).FileName.Should().Be("newest.txt");
        records.Should().OnlyContain(r => r.FileOffset < HomePage0 * PageSize, "these rows were read from the tail copy");
    }

    [Fact]
    public void A_spanning_record_seen_first_in_a_tail_copy_is_completed_from_its_home_pages()
    {
        var log = new LogBuilder();
        var spanning = log.Add(1, redoOp: 0x08, undoOp: 0x08, redo: Enumerable.Repeat((byte)0x5A, 6000).ToArray(), targetVcn: 42, clusterBlock: 0);

        using var ms = new MemoryStream(log.Build());
        var records = NtfsLogFile.Parse(ms).ToList();

        var r = records.Single(x => x.Lsn == spanning);
        r.IsTruncated.Should().BeFalse("the tail copy cannot be followed, but the home pages can");
        r.IsStale.Should().BeFalse();
        r.TargetVcn.Should().Be(42);
    }

    [Fact]
    public void A_hostile_log_page_data_offset_is_ignored()
    {
        var log = new LogBuilder();
        var lsn = log.Add(1, 0x02, 0x03, FileRecord(7, "a.txt", 5), 0, 0);

        // 0xFD0 leaves 48 bytes of payload per page: one megabyte of claimed client data
        // would then need ~21,000 continuation pages.
        using var ms = new MemoryStream(log.Build(dataOffsetField: 0xFD0));
        NtfsLogFile.Parse(ms).ToList().Should().ContainSingle().Which.Lsn.Should().Be(lsn);
    }

    [Fact(Timeout = 120_000)]
    public Task Spanning_records_that_can_never_be_completed_do_not_make_the_parse_quadratic() => Task.Run(() =>
    {
        // Every record page claims a 1 MiB record (260 pages of continuation), and every 250th
        // page is not a record page, so no continuation can ever finish. Without a budget each
        // page re-reads everything up to the next bad page: ~125 page reads per page.
        const int pages = 3000;
        var bytes = new byte[(HomePage0 + pages) * PageSize];
        LogBuilder.WriteRestart(bytes.AsSpan(0, PageSize), DataOffset);
        bytes.AsSpan(0, PageSize).CopyTo(bytes.AsSpan(PageSize));
        var recordPages = 0;
        for (int i = 0; i < pages; i++)
        {
            if (i % 250 == 249)
            {
                continue;
            }
            var page = bytes.AsSpan((HomePage0 + i) * PageSize, PageSize);
            "RCRD"u8.CopyTo(page);
            BinaryPrimitives.WriteUInt16LittleEndian(page[0x18..], PageSize);
            var h = page[DataOffset..];
            BinaryPrimitives.WriteInt64LittleEndian(h, LogBuilder.LsnFor((HomePage0 + i) * (long)PageSize + DataOffset));
            BinaryPrimitives.WriteUInt32LittleEndian(h[0x18..], 1 << 20);
            BinaryPrimitives.WriteUInt32LittleEndian(h[0x20..], 1);
            LogBuilder.Fixup(page, 0x28);
            recordPages++;
        }

        using var counting = new CountingStream(bytes);
        var records = NtfsLogFile.Parse(counting).ToList();

        records.Should().HaveCount(recordPages, "every page's record is still reported");
        records.Should().OnlyContain(r => r.IsTruncated);
        counting.BytesRead.Should().BeLessThan(5L * bytes.Length,
            "continuation reads are budgeted, so total I/O stays linear in the size of the log");
    });

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

        // Records live at their home position in the circular area, which starts after the
        // two restart pages and the two tail-copy pages.
        private long CurrentFileOffset => (HomePage0 + _pages.Count) * (long)PageSize + _pos;

        /// <summary>An LSN is a sequence number above the record's file offset in 8-byte units.</summary>
        internal static long LsnFor(long fileOffset) => (fileOffset / 8) | (7L << (64 - SeqBits));

        public long Add(uint txn, ushort redoOp, ushort undoOp, byte[] redo, long targetVcn, ushort clusterBlock, byte[]? undo = null, long? lsnOverride = null)
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
            var lsn = lsnOverride ?? LsnFor(CurrentFileOffset);
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

        public byte[] Build(string? staleName = null, ushort dataOffsetField = DataOffset)
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
                // An earlier cycle wrote this at the same offset under a lower sequence number.
                BinaryPrimitives.WriteInt64LittleEndian(header, (CurrentFileOffset / 8) | (3L << (64 - SeqBits)));
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18), (uint)body.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x20), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x24), 0x0A);
                header.CopyTo(_page, stalePos);
                body.CopyTo(_page, stalePos + 0x30);
            }
            _pages.Add(_page);

            var restart = new byte[PageSize];
            WriteRestart(restart, dataOffsetField);

            foreach (var p in _pages)
            {
                Fixup(p, 0x28);
            }

            // Pages 2 and 3 are the tail copies: mirrors of the most recently written record
            // pages. With a single record page the second slot has never been used.
            var tail0 = (byte[])_pages[Math.Max(0, _pages.Count - 2)].Clone();
            var tail1 = _pages.Count > 1 ? (byte[])_pages[^1].Clone() : new byte[PageSize];

            var all = new List<byte[]> { restart, (byte[])restart.Clone(), tail0, tail1 };
            all.AddRange(_pages);

            var result = new byte[all.Count * PageSize];
            for (int i = 0; i < all.Count; i++) all[i].CopyTo(result, i * PageSize);
            return result;
        }

        /// <summary>
        /// A restart page laid out as NTFS writes it. Restart area at 0x30:
        /// +0x10 seq_number_bits, +0x18 file_size, +0x24 log_record_header_length,
        /// +0x26 log_page_data_offset.
        /// </summary>
        internal static void WriteRestart(Span<byte> restart, ushort dataOffsetField)
        {
            "RSTR"u8.CopyTo(restart);
            BinaryPrimitives.WriteUInt32LittleEndian(restart[0x10..], PageSize);      // system_page_size
            BinaryPrimitives.WriteUInt32LittleEndian(restart[0x14..], PageSize);      // log_page_size
            BinaryPrimitives.WriteUInt16LittleEndian(restart[0x18..], 0x30);          // restart_area_offset
            var ra = restart[0x30..];
            BinaryPrimitives.WriteInt64LittleEndian(ra, LsnFor(HomePage0 * (long)PageSize + DataOffset));   // current_lsn
            BinaryPrimitives.WriteUInt16LittleEndian(ra[0x08..], 1);                  // log_clients
            BinaryPrimitives.WriteUInt16LittleEndian(ra[0x0A..], 0xFFFF);             // client_free_list
            BinaryPrimitives.WriteUInt16LittleEndian(ra[0x0C..], 0);                  // client_in_use_list
            BinaryPrimitives.WriteUInt16LittleEndian(ra[0x0E..], 2);                  // flags: volume clean
            BinaryPrimitives.WriteUInt32LittleEndian(ra[0x10..], SeqBits);            // seq_number_bits
            BinaryPrimitives.WriteUInt16LittleEndian(ra[0x14..], 0x30);               // restart_area_length
            BinaryPrimitives.WriteUInt16LittleEndian(ra[0x16..], 0x30);               // client_array_offset
            BinaryPrimitives.WriteInt64LittleEndian(ra[0x18..], FileSizeField);       // file_size
            BinaryPrimitives.WriteUInt16LittleEndian(ra[0x24..], 0x30);               // log_record_header_length
            BinaryPrimitives.WriteUInt16LittleEndian(ra[0x26..], dataOffsetField);    // log_page_data_offset
            Fixup(restart, 0x1E);
        }

        internal static void Fixup(Span<byte> page, int usaOfs)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(page[4..], (ushort)usaOfs);
            BinaryPrimitives.WriteUInt16LittleEndian(page[6..], 9);
            BinaryPrimitives.WriteUInt16LittleEndian(page[usaOfs..], Usn);
            for (int i = 1; i <= 8; i++)
            {
                var end = i * 512 - 2;
                page.Slice(end, 2).CopyTo(page[(usaOfs + i * 2)..]);
                BinaryPrimitives.WriteUInt16LittleEndian(page[end..], Usn);
            }
        }

        private static int Pad8(int n) => (n + 7) & ~7;
    }

    /// <summary>Counts the bytes actually read, to pin down how much I/O a parse performs.</summary>
    private sealed class CountingStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = base.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }
    }
}
