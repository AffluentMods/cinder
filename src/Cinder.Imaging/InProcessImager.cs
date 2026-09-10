using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Cinder.Imaging.Ewf;

namespace Cinder.Imaging;

/// <summary>
/// In-process acquisition: a source file, block device or existing container is read once,
/// hashed as it streams, and written either flat (<c>.dd</c>) or as an EnCase-layout EWF chain
/// (<c>.E01</c>, <c>.E02</c>, …) through <see cref="EwfWriter"/>. Unreadable regions are
/// retried, then read sector by sector so a single bad sector costs one sector of zeros rather
/// than a whole block, and every zero-filled sector is counted, listed in the log written
/// beside the image and — for EWF — recorded in the container's <c>error2</c> section.
///
/// <para>Companions: <c>&lt;image&gt;.sha256</c> in <c>sha256sum</c> format for raw output
/// (the Verify tool reads it; EWF carries its digests inside), and <c>&lt;image&gt;.log.json</c>
/// with the job metadata — source, size, digests, bad sectors with offsets, examiner,
/// start/end times, segment list.</para>
/// </summary>
public sealed class InProcessImager : IDiskImager
{
    private const int BlockSize = 1 << 20;
    private const int SectorSize = 512;

    /// <summary>Bad sectors listed individually in the log before the list is capped.</summary>
    private const int MaxLoggedBadSectors = 10_000;

    public static bool Supports(ImageFormat format) => format is ImageFormat.Raw or ImageFormat.Ewf;

    public async Task<ImageJobResult> ImageAsync(ImageJob job, IProgress<ImageJobProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!Supports(job.Format))
        {
            throw new NotSupportedException($"InProcessImager writes raw and EWF images; {job.Format} is not supported in-process.");
        }

        await using var source = OpenSource(job.SourceDevice);
        return await ImageStreamAsync(source, job, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Images an already-open source. Public so the retry / sector-fallback logic can be
    /// exercised against a stream that fails on demand, which a real disk will not do to order.
    /// </summary>
    public async Task<ImageJobResult> ImageStreamAsync(Stream source, ImageJob job, IProgress<ImageJobProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(job);
        if (!Supports(job.Format))
        {
            throw new NotSupportedException($"InProcessImager writes raw and EWF images; {job.Format} is not supported in-process.");
        }

        var started = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        long? total = TryLength(source);

        // EWF records MD5 and SHA-1 in the container; make sure they exist for that format.
        var wantMd5 = job.ComputeMd5 || job.Format == ImageFormat.Ewf;
        var wantSha1 = job.ComputeSha1 || job.Format == ImageFormat.Ewf;
        using var md5 = wantMd5 ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
        using var sha1 = wantSha1 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
        using var sha256 = job.ComputeSha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

        var badSectorOffsets = new List<long>();
        long badSectors = 0;
        long written = 0;
        var block = new byte[BlockSize];

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.OutputPath))!);
        using var sink = OpenSink(job);
        long position = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var (n, newlyBad) = await ReadBlockAsync(source, position, block, job, badSectorOffsets, sink, ct).ConfigureAwait(false);
            badSectors += newlyBad;
            if (n <= 0)
            {
                break;
            }

            md5?.AppendData(block, 0, n);
            sha1?.AppendData(block, 0, n);
            sha256?.AppendData(block, 0, n);
            await sink.WriteAsync(block.AsMemory(0, n), ct).ConfigureAwait(false);

            position += n;
            written += n;
            progress?.Report(new ImageJobProgress(written, total, written / Math.Max(0.001, sw.Elapsed.TotalSeconds), badSectors, "reading"));
        }

        var md5Bytes = md5?.GetHashAndReset();
        var sha1Bytes = sha1?.GetHashAndReset();
        var md5Hex = md5Bytes is null ? null : Convert.ToHexStringLower(md5Bytes);
        var sha1Hex = sha1Bytes is null ? null : Convert.ToHexStringLower(sha1Bytes);
        var sha256Hex = sha256 is null ? null : Convert.ToHexStringLower(sha256.GetHashAndReset());

        sink.Finish(md5Bytes, sha1Bytes);

        // Companions: the digest file the Verify tool reads (raw only), and the acquisition log.
        if (sha256Hex is not null && job.Format == ImageFormat.Raw)
        {
            await File.WriteAllTextAsync(job.OutputPath + ".sha256",
                $"{sha256Hex}  {Path.GetFileName(job.OutputPath)}\n", ct).ConfigureAwait(false);
        }
        var log = new
        {
            tool = "Cinder InProcessImager",
            format = job.Format.ToString(),
            source = job.SourceDevice,
            output = job.OutputPath,
            segments = sink.Paths,
            started_utc = started.ToString("O", CultureInfo.InvariantCulture),
            finished_utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            elapsed_seconds = sw.Elapsed.TotalSeconds,
            bytes_written = written,
            source_length = total,
            md5 = md5Hex,
            sha1 = sha1Hex,
            sha256 = sha256Hex,
            bad_sectors = badSectors,
            bad_sector_offsets = badSectorOffsets,
            bad_sector_list_truncated = badSectors > badSectorOffsets.Count,
            examiner = job.ExaminerName,
            case_number = job.CaseNumber,
            evidence_number = job.EvidenceNumber,
            description = job.Description,
            notes = job.Notes,
        };
        await File.WriteAllTextAsync(job.OutputPath + ".log.json",
            JsonSerializer.Serialize(log, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);

        progress?.Report(new ImageJobProgress(written, total, written / Math.Max(0.001, sw.Elapsed.TotalSeconds), badSectors, "done"));
        return new ImageJobResult(job.OutputPath, written, md5Hex, sha1Hex, sha256Hex, null, badSectors, sw.Elapsed);
    }

    /// <summary>
    /// Fills <paramref name="block"/> from <paramref name="position"/>. A read failure is
    /// retried per the job, then the block is re-read one sector at a time; sectors that still
    /// fail are zero-filled and counted. Returns the number of bytes produced (0 at end).
    /// </summary>
    private static async Task<(int Read, long BadSectors)> ReadBlockAsync(Stream source, long position, byte[] block, ImageJob job,
        List<long> badSectorOffsets, ImageSink sink, CancellationToken ct)
    {
        var attempts = job.ReadErrorRetry ? job.ReadErrorRetries + 1 : 1;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                Seek(source, position);
                return (await ReadFullAsync(source, block, block.Length, ct).ConfigureAwait(false), 0);
            }
            catch (IOException)
            {
                // fall through to retry, then to sector granularity
            }
        }

        // Sector fallback: recover everything readable around the damage.
        var localBad = 0L;
        var produced = 0;
        var sector = new byte[SectorSize];
        for (int off = 0; off < block.Length; off += SectorSize)
        {
            ct.ThrowIfCancellationRequested();
            int n;
            try
            {
                Seek(source, position + off);
                n = await ReadFullAsync(source, sector, SectorSize, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                Array.Clear(sector);
                n = SectorSize;
                localBad++;
                if (badSectorOffsets.Count < MaxLoggedBadSectors)
                {
                    badSectorOffsets.Add(position + off);
                }
                sink.NoteBadSector((position + off) / SectorSize);
            }

            if (n <= 0)
            {
                break;   // genuine end of source inside this block
            }
            Buffer.BlockCopy(sector, 0, block, off, n);
            produced += n;
            if (n < SectorSize)
            {
                break;
            }
        }
        return (produced, localBad);
    }

    private static void Seek(Stream s, long position)
    {
        if (s.CanSeek && s.Position != position)
        {
            s.Position = position;
        }
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buffer, int count, CancellationToken ct)
    {
        var filled = 0;
        while (filled < count)
        {
            var n = await s.ReadAsync(buffer.AsMemory(filled, count - filled), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }
            filled += n;
        }
        return filled;
    }

    private static long? TryLength(Stream s)
    {
        try { return s.CanSeek ? s.Length : null; } catch { return null; }
    }

    /// <summary>
    /// Opens a source for reading. A path that <see cref="EvidenceOpener"/> recognises as an
    /// EWF container is decoded transparently (this is how E01 → raw conversion works); a block
    /// device path (<c>\\.\PhysicalDrive0</c>, <c>/dev/sda</c>) is opened shared and unbuffered
    /// beyond the block size; anything else is a plain file.
    /// </summary>
    public static Stream OpenSource(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (IsDevicePath(sourcePath))
        {
            return new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BlockSize, FileOptions.SequentialScan);
        }
        return EvidenceOpener.Open(sourcePath);
    }

    public static bool IsDevicePath(string path) =>
        path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
        path.StartsWith("/dev/", StringComparison.Ordinal);

    // ---- output sinks --------------------------------------------------------------------

    private static ImageSink OpenSink(ImageJob job) => job.Format switch
    {
        ImageFormat.Ewf => new EwfSink(job),
        _ => new RawSink(job.OutputPath),
    };

    private abstract class ImageSink : IDisposable
    {
        public abstract IReadOnlyList<string> Paths { get; }
        public abstract ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
        public virtual void NoteBadSector(long sector) { }
        public abstract void Finish(byte[]? md5, byte[]? sha1);
        public abstract void Dispose();
    }

    private sealed class RawSink : ImageSink
    {
        private readonly FileStream _out;
        private readonly string _path;

        public RawSink(string path)
        {
            _path = path;
            _out = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BlockSize, FileOptions.SequentialScan);
        }

        public override IReadOnlyList<string> Paths => [_path];
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => _out.WriteAsync(data, ct);
        public override void Finish(byte[]? md5, byte[]? sha1) => _out.Flush();
        public override void Dispose() => _out.Dispose();
    }

    private sealed class EwfSink : ImageSink
    {
        private readonly EwfWriter _writer;

        public EwfSink(ImageJob job)
        {
            var segment = Math.Clamp(job.SegmentSizeMiB, 4, 2000) * 1024L * 1024;
            _writer = new EwfWriter(job.OutputPath, new EwfWriter.Options
            {
                MaxSegmentBytes = segment,
                Compress = job.CompressionLevel > 0,
                Compression = job.CompressionLevel >= 2 ? CompressionLevel.SmallestSize : CompressionLevel.Optimal,
                CaseNumber = job.CaseNumber,
                EvidenceNumber = job.EvidenceNumber,
                Examiner = job.ExaminerName,
                Description = job.Description,
                Notes = job.Notes,
                MediaFlags = IsDevicePath(job.SourceDevice) ? (byte)0x02 : (byte)0x01,
                AcquisitionTool = "Cinder " + (typeof(InProcessImager).Assembly.GetName().Version?.ToString(3) ?? ""),
            });
        }

        public override IReadOnlyList<string> Paths => _writer.SegmentPaths;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            _writer.Write(data.Span);
            return ValueTask.CompletedTask;
        }

        public override void NoteBadSector(long sector) => _writer.AddBadSectors(sector, 1);
        public override void Finish(byte[]? md5, byte[]? sha1) => _writer.Finish(md5, sha1);
        public override void Dispose() => _writer.Dispose();
    }
}

/// <summary>
/// Format conversion is acquisition from a container. E01 → raw decodes every chunk through
/// <see cref="EwfReader"/>, hashes as it streams, writes flat, and compares with the digests
/// the container recorded. Raw → E01 hashes the source as it is chunked and compressed, then
/// re-reads the finished container and checks it reproduces the same digest — so both
/// directions double as a verification.
/// </summary>
public static class ImageConverter
{
    public sealed record ConversionResult(
        ImageJobResult Image,
        string? RecordedMd5,
        string? RecordedSha1,
        int DamagedChunks,
        bool? MatchesRecorded);

    public static async Task<ConversionResult> EwfToRawAsync(
        string sourceE01, string outputRaw, string? examiner = null,
        IProgress<ImageJobProgress>? progress = null, CancellationToken ct = default)
    {
        using var ewf = EwfReader.Open(sourceE01);
        await using var stream = ewf.OpenStream();

        var job = new ImageJob(sourceE01, outputRaw, ImageFormat.Raw, ExaminerName: examiner,
            Description: $"Converted from EWF ({ewf.SegmentCount} segment(s))");
        var result = await new InProcessImager().ImageStreamAsync(stream, job, progress, ct).ConfigureAwait(false);

        bool? matches = null;
        if (ewf.RecordedMd5 is not null && result.Md5 is not null)
        {
            matches = string.Equals(ewf.RecordedMd5, result.Md5, StringComparison.OrdinalIgnoreCase);
        }
        else if (ewf.RecordedSha1 is not null && result.Sha1 is not null)
        {
            matches = string.Equals(ewf.RecordedSha1, result.Sha1, StringComparison.OrdinalIgnoreCase);
        }
        if (ewf.DamagedChunks.Count > 0)
        {
            matches = false;
        }

        return new ConversionResult(result, ewf.RecordedMd5, ewf.RecordedSha1, ewf.DamagedChunks.Count, matches);
    }

    /// <summary>
    /// Raw → E01. <paramref name="verify"/> re-reads the finished chain and hashes it, which
    /// doubles the time and is what turns "written" into "verified"; leave it on unless the
    /// Verify tool will be run separately.
    /// </summary>
    public static async Task<ConversionResult> RawToEwfAsync(
        string sourceRaw, string outputE01, string? examiner = null, int compressionLevel = 1, long segmentSizeMiB = 1500,
        bool verify = true, IProgress<ImageJobProgress>? progress = null, CancellationToken ct = default)
    {
        var job = new ImageJob(sourceRaw, outputE01, ImageFormat.Ewf, CompressionLevel: compressionLevel, SegmentSizeMiB: segmentSizeMiB,
            ExaminerName: examiner, Description: "Converted from raw");
        var result = await new InProcessImager().ImageAsync(job, progress, ct).ConfigureAwait(false);

        if (!verify)
        {
            return new ConversionResult(result, result.Md5, result.Sha1, 0, null);
        }

        using var ewf = EwfReader.Open(outputE01);
        var v = await ewf.VerifyAsync(null, ct).ConfigureAwait(false);
        var matches = v.Verified && string.Equals(ewf.RecordedMd5, result.Md5, StringComparison.OrdinalIgnoreCase);
        return new ConversionResult(result, ewf.RecordedMd5, ewf.RecordedSha1, ewf.DamagedChunks.Count, matches);
    }
}
