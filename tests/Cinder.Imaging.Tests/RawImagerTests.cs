using System.Security.Cryptography;
using System.Text.Json;
using Cinder.Imaging;
using FluentAssertions;
using Xunit;

namespace Cinder.Imaging.Tests;

public sealed class RawImagerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cinder-rawimg").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Images_a_file_byte_for_byte_with_digests_and_companions()
    {
        var src = Path.Combine(_dir, "src.bin");
        var data = EwfBuilder.Media(3 * 1024 * 1024 + 777);
        await File.WriteAllBytesAsync(src, data);
        var outPath = Path.Combine(_dir, "out", "image.dd");

        var progress = new List<ImageJobProgress>();
        var result = await new RawImager().ImageAsync(
            new ImageJob(src, outPath, ImageFormat.Raw, ExaminerName: "alice", CaseNumber: "C-1"),
            new Progress<ImageJobProgress>(progress.Add));

        (await File.ReadAllBytesAsync(outPath)).Should().Equal(data);
        result.BytesWritten.Should().Be(data.Length);
        result.BadSectors.Should().Be(0);
        result.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(data)));
        result.Md5.Should().Be(Convert.ToHexStringLower(MD5.HashData(data)));

        // Companions: sha256sum-format digest and the acquisition log.
        var sums = await File.ReadAllTextAsync(outPath + ".sha256");
        sums.Should().StartWith(result.Sha256 + "  image.dd");
        using var log = JsonDocument.Parse(await File.ReadAllTextAsync(outPath + ".log.json"));
        log.RootElement.GetProperty("examiner").GetString().Should().Be("alice");
        log.RootElement.GetProperty("case_number").GetString().Should().Be("C-1");
        log.RootElement.GetProperty("bytes_written").GetInt64().Should().Be(data.Length);
    }

    [Fact]
    public async Task Bad_sectors_are_zero_filled_counted_and_listed_while_neighbours_survive()
    {
        // 2 MiB source; a 1,536-byte region starting mid-block is unreadable (three sectors).
        var data = EwfBuilder.Media(2 * 1024 * 1024);
        const long badStart = 1_048_576 + 4096;
        const long badLength = 1536;
        var source = new FaultyStream(data, badStart, badLength);
        var outPath = Path.Combine(_dir, "bad.dd");

        var result = await new RawImager().ImageStreamAsync(source,
            new ImageJob("faulty", outPath, ImageFormat.Raw, ReadErrorRetries: 1));

        result.BadSectors.Should().Be(3);
        var written = await File.ReadAllBytesAsync(outPath);
        written.Length.Should().Be(data.Length);
        written.AsSpan((int)badStart, (int)badLength).ToArray().Should().OnlyContain(b => b == 0);
        // Everything outside the damage is intact — including the rest of the damaged block.
        written.AsSpan(0, (int)badStart).ToArray().Should().Equal(data.AsSpan(0, (int)badStart).ToArray());
        written.AsSpan((int)(badStart + badLength)).ToArray().Should().Equal(data.AsSpan((int)(badStart + badLength)).ToArray());

        using var log = JsonDocument.Parse(await File.ReadAllTextAsync(outPath + ".log.json"));
        log.RootElement.GetProperty("bad_sector_offsets").EnumerateArray().Select(e => e.GetInt64())
            .Should().Equal(badStart, badStart + 512, badStart + 1024);
    }

    [Fact]
    public async Task Converts_an_e01_to_raw_and_compares_with_the_recorded_hash()
    {
        var media = EwfBuilder.Media(96 * 1024);
        var e01 = Path.Combine(_dir, "image.E01");
        await File.WriteAllBytesAsync(e01, EwfBuilder.Build(media, new EwfBuilder.Options { Compress = true }));
        var outPath = Path.Combine(_dir, "converted.dd");

        var r = await ImageConverter.EwfToRawAsync(e01, outPath, examiner: "alice");

        (await File.ReadAllBytesAsync(outPath)).Should().Equal(media);
        r.MatchesRecorded.Should().BeTrue();
        r.DamagedChunks.Should().Be(0);
        r.Image.Md5.Should().Be(r.RecordedMd5);
    }

    [Fact]
    public async Task Conversion_of_a_damaged_e01_reports_the_damage_and_does_not_claim_a_match()
    {
        var media = EwfBuilder.Media(4096 * 4);
        var e01 = Path.Combine(_dir, "damaged.E01");
        await File.WriteAllBytesAsync(e01, EwfBuilder.Build(media, new EwfBuilder.Options { Compress = true, CorruptChunkIndex = 1 }));

        var r = await ImageConverter.EwfToRawAsync(e01, Path.Combine(_dir, "damaged.dd"));

        r.DamagedChunks.Should().BeGreaterThan(0);
        r.MatchesRecorded.Should().BeFalse();
        r.Image.BytesWritten.Should().Be(media.Length, "the raw image keeps full geometry with zero-filled damage");
    }

    [Fact]
    public void Device_paths_are_recognised_on_both_platforms()
    {
        RawImager.IsDevicePath(@"\\.\PhysicalDrive0").Should().BeTrue();
        RawImager.IsDevicePath("/dev/sda").Should().BeTrue();
        RawImager.IsDevicePath(@"C:\images\disk.dd").Should().BeFalse();
    }

    [Fact]
    public async Task Refuses_non_raw_formats()
    {
        var act = async () => await new RawImager().ImageAsync(new ImageJob("x", Path.Combine(_dir, "y"), ImageFormat.Ewf));
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    /// <summary>A seekable stream that throws IOException for any read touching [badStart, badStart+badLength).</summary>
    private sealed class FaultyStream(byte[] data, long badStart, long badLength) : Stream
    {
        private long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => _pos = value; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, data.Length - _pos);
            if (n <= 0) return 0;
            var end = _pos + n;
            if (_pos < badStart + badLength && end > badStart)
            {
                throw new IOException("simulated unreadable sector");
            }
            data.AsSpan((int)_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => Length + offset };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
