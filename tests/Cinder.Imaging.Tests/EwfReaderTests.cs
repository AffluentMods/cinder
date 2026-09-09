using System.Security.Cryptography;
using System.Text;
using Cinder.Imaging.Ewf;
using FluentAssertions;
using Xunit;

namespace Cinder.Imaging.Tests;

/// <summary>
/// Coverage for the EWF container reader.
///
/// Everything in an EWF header is attacker-controlled — a forensics tool opens images of
/// unknown provenance by definition — so the malformed-input cases here are as important as
/// the happy path. Each has a timeout: a parser that hangs on hostile input is as bad as one
/// that crashes, and a hang would otherwise wedge the test host rather than fail.
/// </summary>
public sealed class EwfReaderTests
{
    private const int TimeoutMs = 30_000;

    private static Task Guarded(Action body) => Task.Run(body);

    // ---------------------------------------------------------------- happy path ----

    [Fact(Timeout = TimeoutMs)]
    public Task Reads_uncompressed_media_back_byte_for_byte() => Guarded(() =>
    {
        var media = EwfBuilder.Media(64 * 1024);
        var container = EwfBuilder.Build(media);

        using var reader = new EwfReader(new MemoryStream(container));
        reader.MediaSize.Should().Be(media.Length);
        reader.BytesPerSector.Should().Be(512u);

        using var stream = reader.OpenStream();
        var round = ReadAll(stream);
        round.Should().Equal(media);
        reader.DamagedChunks.Should().BeEmpty();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Reads_compressed_media_back_byte_for_byte() => Guarded(() =>
    {
        var media = EwfBuilder.Media(64 * 1024);
        var container = EwfBuilder.Build(media, new EwfBuilder.Options { Compress = true });

        using var reader = new EwfReader(new MemoryStream(container));
        using var stream = reader.OpenStream();
        ReadAll(stream).Should().Equal(media);
        reader.DamagedChunks.Should().BeEmpty();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Reads_a_final_partial_chunk_without_over_reading() => Guarded(() =>
    {
        // 4 KiB chunks, media deliberately not a whole multiple of the chunk size.
        var media = EwfBuilder.Media(4096 * 3 + 512);
        var container = EwfBuilder.Build(media);

        using var reader = new EwfReader(new MemoryStream(container));
        reader.MediaSize.Should().Be(media.Length);

        using var stream = reader.OpenStream();
        ReadAll(stream).Should().Equal(media);
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Exposes_the_recorded_hashes_from_the_digest_section() => Guarded(() =>
    {
        var media = EwfBuilder.Media(8192);
        var container = EwfBuilder.Build(media);

        using var reader = new EwfReader(new MemoryStream(container));
        reader.RecordedMd5.Should().Be(Convert.ToHexStringLower(MD5.HashData(media)));
        reader.RecordedSha1.Should().Be(Convert.ToHexStringLower(SHA1.HashData(media)));
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Seek_and_partial_reads_land_on_the_right_bytes() => Guarded(() =>
    {
        var media = EwfBuilder.Media(32 * 1024);
        var container = EwfBuilder.Build(media, new EwfBuilder.Options { Compress = true });

        using var reader = new EwfReader(new MemoryStream(container));
        using var stream = reader.OpenStream();

        // A read that starts mid-chunk and spans a chunk boundary.
        stream.Position = 4096 - 10;
        var buf = new byte[64];
        var read = stream.Read(buf, 0, buf.Length);
        read.Should().Be(64);
        buf.Should().Equal(media.AsSpan(4096 - 10, 64).ToArray());
    });

    // ------------------------------------------------------------- verification ----

    [Fact(Timeout = TimeoutMs)]
    public async Task Verify_confirms_a_container_whose_recorded_hash_matches()
    {
        var media = EwfBuilder.Media(32 * 1024);
        var container = EwfBuilder.Build(media);

        using var reader = new EwfReader(new MemoryStream(container));
        var result = await reader.VerifyAsync();

        result.Verified.Should().BeTrue();
        result.Md5Match.Should().BeTrue();
        result.Sha1Match.Should().BeTrue();
        result.BytesVerified.Should().Be(media.Length);
        result.Summary().Should().StartWith("Verified");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Verify_rejects_a_container_whose_recorded_hash_is_wrong()
    {
        // The exact case the tool exists to catch: a container asserting a hash that its own
        // contents do not produce. Reading the recorded value off a metadata panel would show
        // a plausible-looking digest and tell the examiner nothing.
        var media = EwfBuilder.Media(32 * 1024);
        var container = EwfBuilder.Build(media, new EwfBuilder.Options { ForcedMd5 = new byte[16] });

        using var reader = new EwfReader(new MemoryStream(container));
        var result = await reader.VerifyAsync();

        result.Verified.Should().BeFalse();
        result.Md5Match.Should().BeFalse();
        result.Sha1Match.Should().BeTrue("only the MD5 was tampered with");
        result.Summary().Should().StartWith("VERIFICATION FAILED");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Verify_reports_unverifiable_when_no_hash_was_recorded()
    {
        // "Nothing to check" must never render as "checked and fine".
        var media = EwfBuilder.Media(8192);
        var container = EwfBuilder.Build(media, new EwfBuilder.Options { IncludeDigest = false });

        using var reader = new EwfReader(new MemoryStream(container));
        var result = await reader.VerifyAsync();

        result.Verified.Should().BeFalse();
        result.Md5Match.Should().BeNull();
        result.Sha1Match.Should().BeNull();
        result.ComputedSha1.Should().Be(Convert.ToHexStringLower(SHA1.HashData(media)));
        result.Summary().Should().StartWith("Unverifiable");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task Verify_reports_progress()
    {
        var media = EwfBuilder.Media(64 * 1024);
        var container = EwfBuilder.Build(media);

        using var reader = new EwfReader(new MemoryStream(container));
        long last = 0;
        await reader.VerifyAsync(new Progress<long>(b => last = Math.Max(last, b)));
        last.Should().BeGreaterThan(0);
    }

    // ------------------------------------------------------------ damaged input ----

    [Fact(Timeout = TimeoutMs)]
    public Task A_corrupt_chunk_is_zero_filled_and_reported_rather_than_truncating_the_stream() => Guarded(() =>
    {
        // Previously a chunk that failed to inflate produced a short buffer, EwfStream turned
        // that into a return of 0, and every caller read it as a clean end-of-media. A hash
        // over the image would then cover only the leading chunks and still look complete.
        var media = EwfBuilder.Media(4096 * 4);
        var container = EwfBuilder.Build(media, new EwfBuilder.Options { Compress = true, CorruptChunkIndex = 1 });

        using var reader = new EwfReader(new MemoryStream(container));
        using var stream = reader.OpenStream();
        var round = ReadAll(stream);

        round.Length.Should().Be(media.Length, "the stream must still yield the full media length");
        reader.DamagedChunks.Should().NotBeEmpty("the damage must be surfaced, not swallowed");
        round.Should().NotEqual(media, "the damaged chunk cannot be recovered");

        // Chunks either side of the damage are intact.
        round.AsSpan(0, 4096).ToArray().Should().Equal(media.AsSpan(0, 4096).ToArray());
        round.AsSpan(8192, 4096).ToArray().Should().Equal(media.AsSpan(8192, 4096).ToArray());
    });

    [Fact(Timeout = TimeoutMs)]
    public async Task Verify_fails_when_chunks_were_damaged()
    {
        var media = EwfBuilder.Media(4096 * 4);
        var container = EwfBuilder.Build(media, new EwfBuilder.Options { Compress = true, CorruptChunkIndex = 2 });

        using var reader = new EwfReader(new MemoryStream(container));
        var result = await reader.VerifyAsync();

        result.Verified.Should().BeFalse();
        result.DamagedChunkCount.Should().BeGreaterThan(0);
        result.Summary().Should().Contain("damaged");
    }

    // ----------------------------------------------------------- hostile input ----

    [Fact(Timeout = TimeoutMs)]
    public Task Rejects_a_file_without_the_EVF_magic() => Guarded(() =>
    {
        var junk = new byte[256];
        var act = () => new EwfReader(new MemoryStream(junk));
        act.Should().Throw<InvalidDataException>().WithMessage("*magic*");
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Rejects_a_table_declaring_more_entries_than_the_section_can_hold() => Guarded(() =>
    {
        // uint.MaxValue entries would previously either allocate ~32 GB for the List capacity
        // or walk off the end of the section buffer.
        var ms = new MemoryStream();
        EwfBuilder.WriteSegmentHeader(ms);

        var volume = new byte[1052];
        BitConverter.GetBytes(1u).CopyTo(volume, 4);
        BitConverter.GetBytes(8u).CopyTo(volume, 8);
        BitConverter.GetBytes(512u).CopyTo(volume, 12);
        BitConverter.GetBytes(8u).CopyTo(volume, 16);
        var volStart = ms.Position;
        EwfBuilder.WriteRawSection(ms, "volume", volume, 76 + volume.Length, volStart + 76 + volume.Length);

        var table = new byte[24 + 4];
        BitConverter.GetBytes(uint.MaxValue).CopyTo(table, 0);   // absurd entry count
        var tblStart = ms.Position;
        EwfBuilder.WriteRawSection(ms, "table", table, 76 + table.Length, tblStart);

        var act = () => new EwfReader(new MemoryStream(ms.ToArray()));
        act.Should().Throw<InvalidDataException>().WithMessage("*entries*");
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Rejects_a_section_declaring_a_size_beyond_the_file() => Guarded(() =>
    {
        var ms = new MemoryStream();
        EwfBuilder.WriteSegmentHeader(ms);
        var start = ms.Position;
        // Declares a 2 GB payload in a file a few hundred bytes long.
        EwfBuilder.WriteRawSection(ms, "header2", new byte[16], declaredSize: 76 + (1L << 31), next: start + 200);

        var act = () => new EwfReader(new MemoryStream(ms.ToArray()));
        act.Should().Throw<InvalidDataException>();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Rejects_a_section_declaring_a_negative_or_undersized_length() => Guarded(() =>
    {
        var ms = new MemoryStream();
        EwfBuilder.WriteSegmentHeader(ms);
        EwfBuilder.WriteRawSection(ms, "header2", new byte[16], declaredSize: -4096, next: 0);

        var act = () => new EwfReader(new MemoryStream(ms.ToArray()));
        act.Should().Throw<InvalidDataException>();
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Terminates_on_a_section_chain_that_points_backwards() => Guarded(() =>
    {
        // Two sections pointing at each other used to loop the parser forever: the old code
        // only treated `next == pos` and `next == 0` as terminators.
        var ms = new MemoryStream();
        EwfBuilder.WriteSegmentHeader(ms);

        var first = ms.Position;
        EwfBuilder.WriteRawSection(ms, "header2", new byte[8], 76 + 8, next: first + 84);
        var second = ms.Position;
        EwfBuilder.WriteRawSection(ms, "header2", new byte[8], 76 + 8, next: first);   // back-edge

        var act = () => new EwfReader(new MemoryStream(ms.ToArray()));
        act.Should().Throw<InvalidDataException>().WithMessage("*backwards*");
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Rejects_implausible_geometry() => Guarded(() =>
    {
        foreach (var (spc, bps) in new[] { (0u, 512u), (8u, 0u), (1u << 21, 512u), (8u, 1u << 21) })
        {
            var ms = new MemoryStream();
            EwfBuilder.WriteSegmentHeader(ms);

            var volume = new byte[1052];
            BitConverter.GetBytes(1u).CopyTo(volume, 4);
            BitConverter.GetBytes(spc).CopyTo(volume, 8);
            BitConverter.GetBytes(bps).CopyTo(volume, 12);
            BitConverter.GetBytes(8u).CopyTo(volume, 16);
            var start = ms.Position;
            EwfBuilder.WriteRawSection(ms, "volume", volume, 76 + volume.Length, start);

            var act = () => new EwfReader(new MemoryStream(ms.ToArray()));
            act.Should().Throw<InvalidDataException>($"geometry {spc}×{bps} is not plausible");
        }
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Rejects_a_chunk_offset_that_points_outside_the_segment() => Guarded(() =>
    {
        var media = EwfBuilder.Media(8192);
        var container = EwfBuilder.Build(media);

        // Rewrite the first table entry to an offset far past the end of the file. The table
        // base is written at table-data offset 8; entries start at 24.
        var tableTypeAt = IndexOf(container, Encoding.ASCII.GetBytes("table\0"));
        tableTypeAt.Should().BeGreaterThan(0);
        var entriesAt = tableTypeAt + 76 + 24;
        BitConverter.GetBytes(0x7FFF_FFFFu).CopyTo(container, entriesAt);

        var act = () => new EwfReader(new MemoryStream(container));
        act.Should().Throw<InvalidDataException>().WithMessage("*outside the file*");
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Tolerates_a_header_section_that_is_not_valid_zlib() => Guarded(() =>
    {
        // Case metadata is not evidence — a broken header must not stop the image opening.
        var media = EwfBuilder.Media(8192);
        var container = EwfBuilder.Build(media);
        var headerAt = IndexOf(container, Encoding.ASCII.GetBytes("header2\0"));
        headerAt.Should().BeGreaterThan(0);
        for (int i = headerAt + 76; i < headerAt + 86; i++)
        {
            container[i] = 0xEE;
        }

        using var reader = new EwfReader(new MemoryStream(container));
        reader.CaseDescription.Should().BeNull();
        using var stream = reader.OpenStream();
        ReadAll(stream).Should().Equal(media);
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Reports_a_volume_and_table_that_disagree_about_chunk_count() => Guarded(() =>
    {
        // Nothing in the format forces the volume section's media size to match the number of
        // entries in the chunk tables. When they disagree the read must say so, not surface an
        // index-out-of-range from somewhere deeper.
        var media = EwfBuilder.Media(4096 * 4);
        var container = EwfBuilder.Build(media);

        // Inflate the sector count so the media claims twice as many chunks as the table holds.
        var volumeAt = IndexOf(container, Encoding.ASCII.GetBytes("volume\0"));
        volumeAt.Should().BeGreaterThan(0);
        BitConverter.GetBytes((uint)(media.Length / 512 * 2)).CopyTo(container, volumeAt + 76 + 16);

        using var reader = new EwfReader(new MemoryStream(container));
        using var stream = reader.OpenStream();

        var act = () => ReadAll(stream);
        act.Should().Throw<InvalidDataException>().WithMessage("*disagree*");
    });

    [Fact(Timeout = TimeoutMs)]
    public Task Rejects_a_container_with_no_table_section() => Guarded(() =>
    {
        var ms = new MemoryStream();
        EwfBuilder.WriteSegmentHeader(ms);

        var volume = new byte[1052];
        BitConverter.GetBytes(1u).CopyTo(volume, 4);
        BitConverter.GetBytes(8u).CopyTo(volume, 8);
        BitConverter.GetBytes(512u).CopyTo(volume, 12);
        BitConverter.GetBytes(8u).CopyTo(volume, 16);
        var start = ms.Position;
        EwfBuilder.WriteRawSection(ms, "volume", volume, 76 + volume.Length, start);

        var act = () => new EwfReader(new MemoryStream(ms.ToArray()));
        act.Should().Throw<InvalidDataException>().WithMessage("*table*");
    });

    // ----------------------------------------------------------- segment naming ----

    [Fact(Timeout = TimeoutMs)]
    public Task DiscoverSegments_stops_at_the_first_gap() => Guarded(() =>
    {
        var dir = Directory.CreateTempSubdirectory("cinder-ewf-seg");
        try
        {
            // E01, E02 present; E03 missing; E04 present but unreachable across the hole.
            foreach (var ext in new[] { ".E01", ".E02", ".E04" })
            {
                File.WriteAllBytes(Path.Combine(dir.FullName, "case" + ext), [0]);
            }

            var found = EwfReader.DiscoverSegments(Path.Combine(dir.FullName, "case.E01"));
            found.Should().HaveCount(2);
            found[0].Should().EndWith("case.E01");
            found[1].Should().EndWith("case.E02");
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { }
        }
    });

    [Fact(Timeout = TimeoutMs)]
    public Task DiscoverSegments_does_not_append_a_stray_letter_segment_to_a_short_chain() => Guarded(() =>
    {
        var dir = Directory.CreateTempSubdirectory("cinder-ewf-seg2");
        try
        {
            foreach (var ext in new[] { ".E01", ".E02", ".EAA" })
            {
                File.WriteAllBytes(Path.Combine(dir.FullName, "case" + ext), [0]);
            }

            // .EAA only continues a chain that filled all 99 numeric slots.
            var found = EwfReader.DiscoverSegments(Path.Combine(dir.FullName, "case.E01"));
            found.Should().HaveCount(2);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { }
        }
    });

    // --------------------------------------------------------------- opener ----

    [Fact(Timeout = TimeoutMs)]
    public Task EvidenceOpener_routes_EWF_to_the_decoded_media_and_raw_files_through() => Guarded(() =>
    {
        var dir = Directory.CreateTempSubdirectory("cinder-ewf-open");
        try
        {
            var media = EwfBuilder.Media(8192);
            var e01 = Path.Combine(dir.FullName, "image.E01");
            File.WriteAllBytes(e01, EwfBuilder.Build(media));

            EvidenceOpener.IsEwf(e01).Should().BeTrue();
            using (var s = EvidenceOpener.Open(e01))
            {
                s.Length.Should().Be(media.Length);
                ReadAll(s).Should().Equal(media);
            }

            var raw = Path.Combine(dir.FullName, "image.dd");
            File.WriteAllBytes(raw, media);
            EvidenceOpener.IsEwf(raw).Should().BeFalse();
            using (var s = EvidenceOpener.Open(raw))
            {
                ReadAll(s).Should().Equal(media);
            }
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { }
        }
    });

    // ------------------------------------------------------------------ helpers ----

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.Position = 0;
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
        => haystack.AsSpan().IndexOf(needle);
}
