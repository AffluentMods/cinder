using System.Buffers.Binary;
using System.Text;
using Cinder.Filesystems;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

public sealed class UsnJournalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 5, 6, 7, 8, TimeSpan.Zero);

    /// <summary>Builds one USN_RECORD_V2 or V3 with the given fields, 8-byte aligned.</summary>
    private static byte[] Record(int version, long fileRef, long parentRef, long usn, DateTimeOffset ts, uint reason, uint attrs, string name)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var refSize = version == 3 ? 16 : 8;
        var header = 8 + refSize * 2 + 8 + 8 + 4 + 4 + 4 + 4 + 2 + 2;
        var length = (header + nameBytes.Length + 7) & ~7;
        var r = new byte[length];
        var s = r.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, length);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], (ushort)version);
        var o = 8;
        BinaryPrimitives.WriteInt64LittleEndian(s[o..], fileRef); o += refSize;
        BinaryPrimitives.WriteInt64LittleEndian(s[o..], parentRef); o += refSize;
        BinaryPrimitives.WriteInt64LittleEndian(s[o..], usn); o += 8;
        BinaryPrimitives.WriteInt64LittleEndian(s[o..], ts.ToFileTime()); o += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(s[o..], reason); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(s[o..], 0); o += 4;        // SourceInfo
        BinaryPrimitives.WriteUInt32LittleEndian(s[o..], 0); o += 4;        // SecurityId
        BinaryPrimitives.WriteUInt32LittleEndian(s[o..], attrs); o += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(s[o..], (ushort)nameBytes.Length); o += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(s[o..], (ushort)header);
        nameBytes.CopyTo(s[header..]);
        return r;
    }

    private static long Ref(long index, int seq) => index | ((long)seq << 48);

    [Fact]
    public void Parses_v2_records_with_reference_split_and_reason_names()
    {
        var journal = Concat(
            Record(2, Ref(1234, 7), Ref(5, 5), 100, T0, 0x00000100 | 0x80000000, 0x20, "evil.exe"),
            Record(2, Ref(1234, 7), Ref(5, 5), 200, T0.AddSeconds(1), 0x00000200 | 0x80000000, 0x20, "evil.exe"));

        var records = UsnJournal.Parse(new MemoryStream(journal)).ToList();

        records.Should().HaveCount(2);
        var r = records[0];
        r.Version.Should().Be(2);
        r.FileName.Should().Be("evil.exe");
        r.MftIndex.Should().Be(1234);
        r.MftSequence.Should().Be(7);
        r.ParentMftIndex.Should().Be(5);
        r.Usn.Should().Be(100);
        r.Timestamp.Should().Be(T0);
        r.ReasonText.Should().Be("FILE_CREATE|CLOSE");
        r.IsDirectory.Should().BeFalse();
        records[1].ReasonText.Should().Be("FILE_DELETE|CLOSE");
    }

    [Fact]
    public void Parses_v3_records_with_128_bit_references()
    {
        var journal = Record(3, Ref(99, 2), Ref(5, 1), 300, T0, 0x2000, 0x10, "renamed-dir");
        var r = UsnJournal.Parse(new MemoryStream(journal)).Single();
        r.Version.Should().Be(3);
        r.MftIndex.Should().Be(99);
        r.MftSequence.Should().Be(2);
        r.FileName.Should().Be("renamed-dir");
        r.IsDirectory.Should().BeTrue();
        r.ReasonText.Should().Be("RENAME_NEW_NAME");
    }

    [Fact]
    public void Skips_zero_padding_between_pages_and_resynchronises_on_garbage()
    {
        var a = Record(2, Ref(1, 1), Ref(5, 1), 1, T0, 0x100, 0x20, "a.txt");
        var padding = new byte[4096 - a.Length];              // page tail
        var garbage = new byte[64];
        Array.Fill(garbage, (byte)0xEE);                      // implausible length field
        var b = Record(2, Ref(2, 1), Ref(5, 1), 2, T0, 0x100, 0x20, "b.txt");

        var records = UsnJournal.Parse(new MemoryStream(Concat(a, padding, garbage, b))).ToList();

        records.Select(r => r.FileName).Should().Equal("a.txt", "b.txt");
    }

    [Fact]
    public void Ignores_v4_range_records_and_a_truncated_tail()
    {
        var v2 = Record(2, Ref(1, 1), Ref(5, 1), 1, T0, 0x100, 0x20, "keep.txt");
        var v4 = Record(2, Ref(3, 1), Ref(5, 1), 3, T0, 0x100, 0x20, "x");
        BinaryPrimitives.WriteUInt16LittleEndian(v4.AsSpan(4), 4);   // claim V4
        var truncated = Record(2, Ref(4, 1), Ref(5, 1), 4, T0, 0x100, 0x20, "cut-off.txt")[..30];

        var records = UsnJournal.Parse(new MemoryStream(Concat(v2, v4, truncated))).ToList();

        records.Should().ContainSingle().Which.FileName.Should().Be("keep.txt");
    }

    [Fact]
    public void A_name_offset_pointing_outside_the_record_is_rejected_not_read()
    {
        var r = Record(2, Ref(1, 1), Ref(5, 1), 1, T0, 0x100, 0x20, "a.txt");
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(58), 60_000);   // FileNameLength way past the record
        UsnJournal.Parse(new MemoryStream(r)).Should().BeEmpty();
    }

    [Fact]
    public void Reason_text_lists_unknown_bits_in_hex()
    {
        UsnJournal.DescribeReason(0x00000100 | 0x40000000).Should().Be("FILE_CREATE|0x40000000");
        UsnJournal.DescribeReason(0).Should().Be("");
    }

    [Fact]
    public void Large_journal_streams_without_buffering_it_whole()
    {
        // 3,000 records across several 1 MiB parser windows, with page padding every 4 KiB.
        var ms = new MemoryStream();
        var inPage = 0;
        for (int i = 0; i < 3000; i++)
        {
            var rec = Record(2, Ref(i, 1), Ref(5, 1), i, T0.AddMilliseconds(i), 0x2, 0x20, $"file-{i:D5}.bin");
            if (inPage + rec.Length > 4096)
            {
                ms.Write(new byte[4096 - inPage]);
                inPage = 0;
            }
            ms.Write(rec);
            inPage += rec.Length;
        }
        ms.Position = 0;

        var records = UsnJournal.Parse(ms).ToList();
        records.Should().HaveCount(3000);
        records[^1].FileName.Should().Be("file-02999.bin");
        records.Select(r => r.Usn).Should().BeInAscendingOrder();
    }

    // ---- termination and hostile lengths ----------------------------------------------------
    //
    // These run on a timed task: the failure mode they guard against is a parse that never
    // returns, which would otherwise hang the test host instead of failing a test.

    private const int TimeoutMs = 30_000;

    [Theory(Timeout = TimeoutMs)]
    [InlineData(60)]
    [InlineData(64)]
    [InlineData(80)]
    public Task A_final_record_cut_off_after_its_fixed_header_does_not_hang(int keep) => Task.Run(() =>
    {
        // 60 or more bytes of a record whose length field says 88. The old loop only stopped
        // when fewer than 60 bytes were left, so this spun forever with nothing to consume
        // and nothing to read.
        var whole = Record(2, Ref(1, 1), Ref(5, 1), 1, T0, 0x100, 0x20, "keep.txt");
        var cut = Record(2, Ref(4, 1), Ref(5, 1), 4, T0, 0x100, 0x20, "cut-off-name.txt");
        cut.Length.Should().BeGreaterThan(keep);

        var records = UsnJournal.Parse(new MemoryStream(Concat(whole, cut[..keep]))).ToList();

        records.Should().ContainSingle().Which.FileName.Should().Be("keep.txt");
    });

    [Fact(Timeout = TimeoutMs)]
    public Task A_stream_that_ends_before_its_advertised_length_does_not_hang() => Task.Run(() =>
    {
        // What a truncated image looks like through a stream layer that reports the size the
        // filesystem recorded: Length says more than Read will ever deliver.
        var whole = Record(2, Ref(1, 1), Ref(5, 1), 1, T0, 0x100, 0x20, "keep.txt");
        var cut = Record(2, Ref(4, 1), Ref(5, 1), 4, T0, 0x100, 0x20, "cut-off-name.txt")[..70];

        using var lying = new LyingLengthStream(Concat(whole, cut), advertised: 1 << 20);
        var records = UsnJournal.Parse(lying).ToList();

        records.Should().ContainSingle().Which.FileName.Should().Be("keep.txt");
    });

    [Fact(Timeout = TimeoutMs)]
    public Task A_length_field_that_overruns_the_end_does_not_hide_the_records_after_it() => Task.Run(() =>
    {
        // Eight bytes that read as "a 4096-byte record starts here", close enough to the end
        // that it can never be satisfied. The real records behind it must still be returned.
        var a = Record(2, Ref(1, 1), Ref(5, 1), 1, T0, 0x100, 0x20, "a.txt");
        var planted = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(planted, 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(planted.AsSpan(4), 2);
        var b = Record(2, Ref(2, 1), Ref(5, 1), 2, T0, 0x100, 0x20, "b.txt");

        var records = UsnJournal.Parse(new MemoryStream(Concat(a, planted, b))).ToList();

        records.Select(r => r.FileName).Should().Equal("a.txt", "b.txt");
    });

    [Theory(Timeout = TimeoutMs)]
    [InlineData(64)]
    [InlineData(72)]
    public Task A_v3_record_shorter_than_the_v3_header_is_skipped_and_parsing_continues(int claimedLength) => Task.Run(() =>
    {
        // A V3 header is 76 bytes. A record that says "V3, 64 bytes" passed the V2-sized length
        // check and was then read past its end, which threw and — because every caller treats
        // an exception as "unreadable volume" — silently dropped the rest of the journal.
        var before = Record(2, Ref(1, 1), Ref(5, 1), 1, T0, 0x100, 0x20, "before.txt");
        var shortV3 = new byte[claimedLength];
        BinaryPrimitives.WriteInt32LittleEndian(shortV3, claimedLength);
        BinaryPrimitives.WriteUInt16LittleEndian(shortV3.AsSpan(4), 3);
        var after = Record(3, Ref(2, 1), Ref(5, 1), 2, T0, 0x100, 0x20, "after.txt");

        List<UsnRecord>? records = null;
        var act = () => records = UsnJournal.Parse(new MemoryStream(Concat(before, shortV3, after))).ToList();

        act.Should().NotThrow();
        records!.Select(r => r.FileName).Should().Equal("before.txt", "after.txt");
        UsnJournal.TryParseRecord(shortV3, out _).Should().BeFalse();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task A_non_seekable_stream_is_parsed_front_to_back() => Task.Run(() =>
    {
        var a = Record(2, Ref(1, 1), Ref(5, 1), 1, T0, 0x100, 0x20, "a.txt");
        var b = Record(3, Ref(2, 1), Ref(5, 1), 2, T0, 0x100, 0x20, "b.txt");

        using var forward = new LyingLengthStream(Concat(a, b), advertised: -1);
        UsnJournal.Parse(forward).Select(r => r.FileName).Should().Equal("a.txt", "b.txt");
    });

    [Fact(Timeout = TimeoutMs)]
    public async Task Cancellation_is_observed()
    {
        var a = Record(2, Ref(1, 1), Ref(5, 1), 1, T0, 0x100, 0x20, "a.txt");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => UsnJournal.Parse(new MemoryStream(a), cts.Token).ToList();
        act.Should().Throw<OperationCanceledException>();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    /// <summary>
    /// Read-only stream whose <see cref="Length"/> is whatever the test says. A negative
    /// <c>advertised</c> makes it forward-only (no Length, no Position).
    /// </summary>
    private sealed class LyingLengthStream(byte[] data, long advertised) : Stream
    {
        private long _pos;

        public override bool CanRead => true;
        public override bool CanSeek => advertised >= 0;
        public override bool CanWrite => false;
        public override long Length => advertised >= 0 ? advertised : throw new NotSupportedException();

        public override long Position
        {
            get => advertised >= 0 ? _pos : throw new NotSupportedException();
            set => _pos = advertised >= 0 ? value : throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Max(0, Math.Min(count, data.Length - _pos));
            if (n == 0)
            {
                return 0;
            }
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
