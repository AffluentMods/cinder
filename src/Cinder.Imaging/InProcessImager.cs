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
/// retried, then read in 4 KiB units and finally sector by sector so a single bad sector costs
/// one sector of zeros rather than a whole block, and every zero-filled sector is counted,
/// listed in the log written beside the image and — for EWF — recorded in the container's
/// <c>error2</c> section.
///
/// <para><b>Nothing unfinished sits at the output path.</b> Every format is written to
/// <c>&lt;output&gt;.partial</c> (each EWF segment to <c>&lt;segment&gt;.partial</c>) and renamed
/// only when the image is complete. A cancelled job deletes its partial files. A job that fails
/// keeps them — what was recovered from a dying disk is still worth having — under the
/// <c>.partial</c> name, with a log that says the image is incomplete and why.</para>
///
/// <para><b>A source that stops answering ends the job.</b> Zero-filling is for bad sectors, not
/// for a disk that has dropped off the bus: after <see cref="MaxConsecutiveFailedBlocks"/>
/// blocks in a row without one readable sector the acquisition is aborted with
/// <see cref="AcquisitionAbortedException"/>, and no sector past the source's known length is
/// ever zero-filled.</para>
///
/// <para><b>Sector-granular formats.</b> EWF, VHD and VHDX store whole 512-byte sectors. When
/// the source does not end on a sector boundary the image is padded with zeros and the padding
/// is hashed, so the recorded digests are those of the stored media and any verifier (Cinder's,
/// ewfverify, FTK Imager) reproduces them. The log records the logical source length, the
/// padding, and the digests of the unpadded source.</para>
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

    /// <summary>
    /// First step down from a failed block. 4 KiB is the logical sector size of 4Kn drives, on
    /// which a 512-byte read is refused outright, and a whole number of sectors on every other
    /// drive — so this size is readable wherever the medium is, whatever the sector size.
    /// </summary>
    private const int CoarseUnit = 4096;

    /// <summary>Bad sectors listed individually in the log before the list is capped.</summary>
    private const int MaxLoggedBadSectors = 10_000;

    /// <summary>Suffix carried by every output file until the image is complete.</summary>
    public const string PartialSuffix = ".partial";

    /// <summary>
    /// Consecutive 1 MiB blocks without a single readable sector after which the source is
    /// taken to have failed or been disconnected, and the acquisition is aborted rather than
    /// zero-filled to the end. 64 leaves room for a 64 MiB dead region on a damaged disk.
    /// </summary>
    public int MaxConsecutiveFailedBlocks { get; init; } = 64;

    public static bool Supports(ImageFormat format) =>
        format is ImageFormat.Raw or ImageFormat.Ewf or ImageFormat.Vhd or ImageFormat.Vhdx or ImageFormat.Aff4;

    public async Task<ImageJobResult> ImageAsync(ImageJob job, IProgress<ImageJobProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!Supports(job.Format))
        {
            throw new NotSupportedException($"InProcessImager writes raw, EWF, AFF4, VHD and VHDX images; {job.Format} is not supported in-process.");
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
            throw new NotSupportedException($"InProcessImager writes raw, EWF, AFF4, VHD and VHDX images; {job.Format} is not supported in-process.");
        }
        if (MaxConsecutiveFailedBlocks < 1)
        {
            throw new InvalidOperationException($"{nameof(MaxConsecutiveFailedBlocks)} must be at least 1.");
        }

        var started = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        long? total = TryLength(source);

        // EWF and AFF4 record digests in the container; make sure they exist for those formats.
        var container = job.Format is ImageFormat.Ewf or ImageFormat.Aff4;
        var wantMd5 = job.ComputeMd5 || container;
        var wantSha1 = job.ComputeSha1 || container;
        using var md5 = wantMd5 ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
        using var sha1 = wantSha1 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
        using var sha256 = job.ComputeSha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

        var badSectorOffsets = new List<long>();
        long badSectors = 0;
        long written = 0;
        var padding = 0;
        long sourceDamaged = 0;
        string? md5Hex = null, sha1Hex = null, sha256Hex = null;
        string? logicalMd5 = null, logicalSha1 = null, logicalSha256 = null;
        var block = new byte[BlockSize];

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.OutputPath))!);
        var sink = OpenSink(job, total);

        object BuildLog(bool complete, string? error) => new
        {
            tool = "Cinder InProcessImager",
            format = job.Format.ToString(),
            source = job.SourceDevice,
            output = job.OutputPath,
            complete,
            status = complete ? "complete" : "incomplete",
            error,
            segments = complete ? sink.Paths : sink.PartialPaths.Where(File.Exists).ToList(),
            started_utc = started.ToString("O", CultureInfo.InvariantCulture),
            finished_utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            elapsed_seconds = sw.Elapsed.TotalSeconds,
            bytes_written = written,
            logical_source_bytes = written,
            padding_bytes = padding,
            image_media_bytes = written + padding,
            source_length = total,
            md5 = md5Hex,
            sha1 = sha1Hex,
            sha256 = sha256Hex,
            logical_md5 = logicalMd5 ?? md5Hex,
            logical_sha1 = logicalSha1 ?? sha1Hex,
            logical_sha256 = logicalSha256 ?? sha256Hex,
            bad_sectors = badSectors,
            bad_sector_offsets = badSectorOffsets,
            bad_sector_list_truncated = badSectors > badSectorOffsets.Count,
            bad_sectors_not_recorded_in_container = sink.BadSectorsNotRecorded,
            overwrite = job.Overwrite,
            removed_stale_segments = sink.RemovedStaleSegments,
            source_damaged_chunks = sourceDamaged,
            examiner = job.ExaminerName,
            case_number = job.CaseNumber,
            evidence_number = job.EvidenceNumber,
            description = job.Description,
            notes = job.Notes,
        };

        // The log is small and describes work already done; it is written whether or not the
        // caller has since cancelled.
        Task WriteLogAsync(object log) => File.WriteAllTextAsync(job.OutputPath + ".log.json",
            JsonSerializer.Serialize(log, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);

        try
        {
            long position = 0;
            var failedRun = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var read = await ReadBlockAsync(source, position, block, total, job, badSectorOffsets, sink, ct).ConfigureAwait(false);
                badSectors += read.BadSectors;
                var n = read.Bytes;
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

                if (!read.EntirelyUnreadable)
                {
                    failedRun = 0;
                }
                else if (++failedRun >= MaxConsecutiveFailedBlocks)
                {
                    throw new AcquisitionAbortedException(
                        $"Acquisition aborted at offset {position:N0}: the last {failedRun} blocks ({(long)failedRun * BlockSize:N0} bytes) " +
                        "had no readable sector, so the source has failed or been disconnected. " +
                        $"The image is INCOMPLETE; what was read is in {string.Join(", ", sink.PartialPaths)}.",
                        written, sink.PartialPaths);
                }
            }

            // Sector-granular formats store whole sectors. Pad here, and hash the padding, so
            // the digests describe the media the container actually holds.
            padding = (int)((sink.SectorGranularity - (written % sink.SectorGranularity)) % sink.SectorGranularity);
            if (padding > 0)
            {
                logicalMd5 = md5 is null ? null : Convert.ToHexStringLower(md5.GetCurrentHash());
                logicalSha1 = sha1 is null ? null : Convert.ToHexStringLower(sha1.GetCurrentHash());
                logicalSha256 = sha256 is null ? null : Convert.ToHexStringLower(sha256.GetCurrentHash());

                var zeros = new byte[padding];
                md5?.AppendData(zeros);
                sha1?.AppendData(zeros);
                sha256?.AppendData(zeros);
                await sink.WriteAsync(zeros, ct).ConfigureAwait(false);
            }

            var md5Bytes = md5?.GetHashAndReset();
            var sha1Bytes = sha1?.GetHashAndReset();
            var sha256Bytes = sha256?.GetHashAndReset();
            md5Hex = md5Bytes is null ? null : Convert.ToHexStringLower(md5Bytes);
            sha1Hex = sha1Bytes is null ? null : Convert.ToHexStringLower(sha1Bytes);
            sha256Hex = sha256Bytes is null ? null : Convert.ToHexStringLower(sha256Bytes);

            // A container source zero-fills chunks it cannot decode. Those are not bad sectors
            // of this acquisition, but the image now holds filler where evidence was.
            sourceDamaged = (source as IEvidenceDamageSource)?.DamagedChunks.Count ?? 0;

            ct.ThrowIfCancellationRequested();
            sink.Finish(md5Bytes, sha1Bytes, sha256Bytes);
        }
        catch (OperationCanceledException)
        {
            sink.Dispose();
            sink.DeletePartial();
            throw;
        }
        catch (Exception ex)
        {
            sink.Dispose();
            try { await WriteLogAsync(BuildLog(complete: false, error: ex.Message)).ConfigureAwait(false); }
            catch { /* the original failure is the one to report */ }
            throw;
        }
        finally
        {
            sink.Dispose();
        }

        // Companions: the digest file the Verify tool reads (formats that do not carry their
        // own digest), and the acquisition log. The image is finished and in place by now, so
        // these are written regardless of a late cancellation.
        if (sha256Hex is not null && !container)
        {
            await File.WriteAllTextAsync(job.OutputPath + ".sha256",
                $"{sha256Hex}  {Path.GetFileName(job.OutputPath)}\n", CancellationToken.None).ConfigureAwait(false);
        }
        await WriteLogAsync(BuildLog(complete: true, error: null)).ConfigureAwait(false);

        progress?.Report(new ImageJobProgress(written, total, written / Math.Max(0.001, sw.Elapsed.TotalSeconds), badSectors, "done"));
        return new ImageJobResult(job.OutputPath, written, md5Hex, sha1Hex, sha256Hex, null, badSectors, sw.Elapsed,
            SourceDamagedChunks: sourceDamaged, PaddingBytes: padding);
    }

    /// <summary>What one pass of <see cref="ReadBlockAsync"/> produced.</summary>
    /// <param name="Bytes">Bytes placed in the block; 0 at the end of the source.</param>
    /// <param name="BadSectors">Sectors that could not be read and were zero-filled.</param>
    /// <param name="EntirelyUnreadable">True when bytes were produced and every one of them is filler.</param>
    private readonly record struct BlockRead(int Bytes, long BadSectors, bool EntirelyUnreadable);

    /// <summary>
    /// Fills <paramref name="block"/> from <paramref name="position"/>. A read failure is
    /// retried per the job, then the block is re-read in 4 KiB units, and a unit that still
    /// fails one sector at a time; sectors that cannot be read are zero-filled and counted.
    ///
    /// <para><paramref name="total"/>, when known, bounds the damage handling: a read is never
    /// asked to cross the end of the source, and a failure at or past the end is the end of the
    /// source rather than bad sectors — raw devices on Windows report an error there instead of
    /// a zero-length read. The source is still probed past its declared length, so one that
    /// turns out longer (a file that grew) is read to its real end.</para>
    /// </summary>
    private static async Task<BlockRead> ReadBlockAsync(Stream source, long position, byte[] block, long? total, ImageJob job,
        List<long> badSectorOffsets, ImageSink sink, CancellationToken ct)
    {
        var want = block.Length;
        var pastDeclaredEnd = false;
        if (total is { } length)
        {
            if (position < length)
            {
                want = (int)Math.Min(want, length - position);
            }
            else
            {
                pastDeclaredEnd = true;
            }
        }

        var attempts = job.ReadErrorRetry ? job.ReadErrorRetries + 1 : 1;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                Seek(source, position);
                return new BlockRead(await ReadFullAsync(source, block.AsMemory(0, want), ct).ConfigureAwait(false), 0, false);
            }
            catch (IOException) when (pastDeclaredEnd)
            {
                return new BlockRead(0, 0, false);
            }
            catch (IOException)
            {
                // fall through to retry, then to finer granularity
            }
        }

        // Fallback: recover everything readable around the damage.
        var localBad = 0L;
        var produced = 0;
        var readable = 0;
        var ended = false;
        for (int off = 0; off < want && !ended; off += CoarseUnit)
        {
            ct.ThrowIfCancellationRequested();
            var unit = Math.Min(CoarseUnit, want - off);
            int n;
            try
            {
                Seek(source, position + off);
                n = await ReadFullAsync(source, block.AsMemory(off, unit), ct).ConfigureAwait(false);
                readable += n;
            }
            catch (IOException)
            {
                // Something in this unit is unreadable; find out which sectors.
                n = 0;
                for (int so = 0; so < unit; so += SectorSize)
                {
                    var len = Math.Min(SectorSize, unit - so);
                    var at = position + off + so;
                    int m;
                    try
                    {
                        Seek(source, at);
                        m = await ReadFullAsync(source, block.AsMemory(off + so, len), ct).ConfigureAwait(false);
                        readable += m;
                    }
                    catch (IOException)
                    {
                        block.AsSpan(off + so, len).Clear();
                        m = len;
                        localBad++;
                        if (badSectorOffsets.Count < MaxLoggedBadSectors)
                        {
                            badSectorOffsets.Add(at);
                        }
                        sink.NoteBadSector(at / SectorSize);
                    }

                    n += m;
                    if (m < len)
                    {
                        break;   // genuine end of source inside this sector
                    }
                }
            }

            produced += n;
            if (n < unit)
            {
                ended = true;   // genuine end of source inside this block
            }
        }
        return new BlockRead(produced, localBad, produced > 0 && readable == 0);
    }

    private static void Seek(Stream s, long position)
    {
        if (s.CanSeek && s.Position != position)
        {
            s.Position = position;
        }
    }

    private static async Task<int> ReadFullAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var n = await s.ReadAsync(buffer[filled..], ct).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }
            filled += n;
        }
        return filled;
    }

    /// <summary>
    /// The source's length when it reports a usable one. Zero is treated as unknown: block
    /// devices and other special files can report a length of 0 while holding data, and
    /// believing it would end the acquisition before the first byte.
    /// </summary>
    private static long? TryLength(Stream s)
    {
        try
        {
            if (!s.CanSeek)
            {
                return null;
            }
            var length = s.Length;
            return length > 0 ? length : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Opens a source for reading. A path that <see cref="EvidenceOpener"/> recognises as an
    /// EWF container is decoded transparently (this is how E01 → raw conversion works); a block
    /// device path (<c>\\.\PhysicalDrive0</c>, <c>/dev/sda</c>) is opened shared and unbuffered;
    /// anything else is a plain file.
    /// </summary>
    public static Stream OpenSource(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (IsDevicePath(sourcePath))
        {
            // No FileStream buffer on a device. With one, a single-sector read during damage
            // recovery is really a buffer-sized read ahead: it fails for every readable sector
            // that precedes a bad one, and those sectors are then zero-filled as if they were
            // bad too. Unbuffered, each read reaches the device exactly as asked.
            return new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0, FileOptions.None);
        }
        return EvidenceOpener.Open(sourcePath);
    }

    public static bool IsDevicePath(string path) =>
        path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
        path.StartsWith("/dev/", StringComparison.Ordinal);

    // ---- output sinks --------------------------------------------------------------------

    private static ImageSink OpenSink(ImageJob job, long? sourceLength) => job.Format switch
    {
        ImageFormat.Ewf => new EwfSink(job),
        ImageFormat.Aff4 => new Aff4Sink(job),
        ImageFormat.Vhd or ImageFormat.Vhdx => new VirtualDiskSink(job, sourceLength),
        _ => new RawSink(job.OutputPath, job.Overwrite),
    };

    /// <summary>
    /// Refuses to start when the image, or the working file of an earlier run, is already
    /// there and the job did not ask for it to be replaced. Called before anything is created.
    /// </summary>
    private static void RefuseIfExists(bool overwrite, string output)
    {
        if (overwrite)
        {
            return;
        }
        var existing = new[] { output, output + PartialSuffix }.Where(File.Exists).ToList();
        if (existing.Count == 0)
        {
            return;
        }
        var what = existing[0].EndsWith(PartialSuffix, StringComparison.Ordinal) && existing.Count == 1
            ? $"{existing[0]} already exists — the unfinished image of an earlier run"
            : $"{existing[0]} already exists";
        throw new OutputExistsException(
            $"{what}. Nothing was written. Choose another output, move the existing file away, or set {nameof(ImageJob)}.{nameof(ImageJob.Overwrite)}.",
            existing);
    }

    /// <summary>
    /// A format writer. Everything is written to <see cref="PartialPaths"/>;
    /// <see cref="Finish"/> completes the image and moves it to <see cref="Paths"/>. Disposing
    /// without finishing leaves only the partial files.
    /// </summary>
    private abstract class ImageSink : IDisposable
    {
        /// <summary>The files of the finished image.</summary>
        public abstract IReadOnlyList<string> Paths { get; }

        /// <summary>Where the data is until <see cref="Finish"/> succeeds.</summary>
        public abstract IReadOnlyList<string> PartialPaths { get; }

        /// <summary>The media must be a whole multiple of this many bytes (1 = any length).</summary>
        public virtual int SectorGranularity => 1;

        /// <summary>Bad sectors the container format had no way to record.</summary>
        public virtual long BadSectorsNotRecorded => 0;

        /// <summary>Files of an overwritten image that were deleted because the new image did not reuse them.</summary>
        public virtual IReadOnlyList<string> RemovedStaleSegments => [];

        public abstract ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
        public virtual void NoteBadSector(long sector) { }
        public abstract void Finish(byte[]? md5, byte[]? sha1, byte[]? sha256);
        public abstract void Dispose();

        /// <summary>Removes whatever an unfinished run left behind. Call after <see cref="Dispose"/>.</summary>
        public void DeletePartial()
        {
            foreach (var p in PartialPaths)
            {
                try { File.Delete(p); } catch { /* best effort; the name still says partial */ }
            }
        }
    }

    /// <summary>
    /// VHD / VHDX through DiscUtils' managed writers. Dynamic disks, so unallocated zero
    /// runs cost nothing on disk. Both formats need the capacity up front, which is why a
    /// source of unknown length cannot go to these formats — image it raw or E01 first.
    /// </summary>
    private sealed class VirtualDiskSink : ImageSink
    {
        private readonly FileStream _file;
        private readonly DiscUtils.VirtualDisk _disk;
        private readonly Stream _content;
        private readonly string _path;
        private readonly bool _overwrite;

        public VirtualDiskSink(ImageJob job, long? sourceLength)
        {
            if (sourceLength is not { } capacity || capacity <= 0)
            {
                throw new NotSupportedException($"{job.Format} needs the source size up front; this source does not report one. Image it raw or E01.");
            }
            // Both formats are sector-granular; round a ragged tail up so the last bytes fit.
            capacity = (capacity + 511) / 512 * 512;

            _path = job.OutputPath;
            _overwrite = job.Overwrite;
            RefuseIfExists(_overwrite, _path);
            _file = new FileStream(_path + PartialSuffix, _overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BlockSize);
            _disk = job.Format == ImageFormat.Vhd
                ? DiscUtils.Vhd.Disk.InitializeDynamic(_file, DiscUtils.Streams.Ownership.None, capacity)
                : DiscUtils.Vhdx.Disk.InitializeDynamic(_file, DiscUtils.Streams.Ownership.None, capacity);
            _content = _disk.Content;
            _content.Position = 0;
        }

        public override IReadOnlyList<string> Paths => [_path];
        public override IReadOnlyList<string> PartialPaths => [_path + PartialSuffix];
        public override int SectorGranularity => SectorSize;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            _content.Write(data.Span);
            return ValueTask.CompletedTask;
        }

        public override void Finish(byte[]? md5, byte[]? sha1, byte[]? sha256)
        {
            // DiscUtils' VHDX content stream does not implement Flush; disposing the disk
            // writes the block allocation table and metadata for both formats.
            _disk.Dispose();
            _file.Flush();
            _file.Dispose();
            File.Move(_path + PartialSuffix, _path, overwrite: _overwrite);
        }

        public override void Dispose()
        {
            try { _disk.Dispose(); } catch { }
            _file.Dispose();
        }
    }

    private sealed class Aff4Sink : ImageSink
    {
        private readonly Aff4.Aff4Writer _writer;
        private readonly string _path;
        private readonly bool _overwrite;

        public Aff4Sink(ImageJob job)
        {
            _path = Path.GetFullPath(job.OutputPath);
            _overwrite = job.Overwrite;
            RefuseIfExists(_overwrite, _path);
            _writer = new Aff4.Aff4Writer(_path + PartialSuffix, new Aff4.Aff4Writer.Options
            {
                Overwrite = _overwrite,
                Compress = job.CompressionLevel > 0,
                CaseNumber = job.CaseNumber,
                EvidenceNumber = job.EvidenceNumber,
                Examiner = job.ExaminerName,
                Description = job.Description,
                Notes = job.Notes,
                Source = job.SourceDevice,
            });
        }

        public override IReadOnlyList<string> Paths => [_path];
        public override IReadOnlyList<string> PartialPaths => [_path + PartialSuffix];

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            _writer.Write(data.Span);
            return ValueTask.CompletedTask;
        }

        public override void Finish(byte[]? md5, byte[]? sha1, byte[]? sha256)
        {
            _writer.Finish(md5, sha1, sha256);
            File.Move(_path + PartialSuffix, _path, overwrite: _overwrite);
        }

        public override void Dispose() => _writer.Dispose();
    }

    private sealed class RawSink : ImageSink
    {
        private readonly FileStream _out;
        private readonly string _path;
        private readonly bool _overwrite;

        public RawSink(string path, bool overwrite)
        {
            _path = path;
            _overwrite = overwrite;
            RefuseIfExists(overwrite, path);
            _out = new FileStream(path + PartialSuffix, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, BlockSize, FileOptions.SequentialScan);
        }

        public override IReadOnlyList<string> Paths => [_path];
        public override IReadOnlyList<string> PartialPaths => [_path + PartialSuffix];
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => _out.WriteAsync(data, ct);

        public override void Finish(byte[]? md5, byte[]? sha1, byte[]? sha256)
        {
            _out.Flush();
            _out.Dispose();
            File.Move(_path + PartialSuffix, _path, overwrite: _overwrite);
        }

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
                PartialSuffix = PartialSuffix,
                Overwrite = job.Overwrite,
            });
        }

        public override IReadOnlyList<string> Paths => _writer.SegmentPaths;
        public override IReadOnlyList<string> PartialPaths => _writer.WorkingPaths;
        public override int SectorGranularity => SectorSize;
        public override long BadSectorsNotRecorded => _writer.BadSectorsNotRecorded;
        public override IReadOnlyList<string> RemovedStaleSegments => _writer.RemovedStaleSegments;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            _writer.Write(data.Span);
            return ValueTask.CompletedTask;
        }

        public override void NoteBadSector(long sector) => _writer.AddBadSectors(sector, 1);
        public override void Finish(byte[]? md5, byte[]? sha1, byte[]? sha256) => _writer.Finish(md5, sha1);
        public override void Dispose() => _writer.Dispose();
    }
}

/// <summary>
/// Thrown when an acquisition is stopped because the source stopped returning data — block
/// after block with no readable sector — rather than carried on as an image of zeros. The
/// partial image is kept under its <c>.partial</c> name and the acquisition log marks it
/// incomplete.
/// </summary>
public sealed class AcquisitionAbortedException : IOException
{
    public AcquisitionAbortedException(string message, long bytesAcquired, IReadOnlyList<string> partialPaths)
        : base(message)
    {
        BytesAcquired = bytesAcquired;
        PartialPaths = partialPaths;
    }

    /// <summary>Bytes written to the partial image, including zero-filled sectors.</summary>
    public long BytesAcquired { get; }

    /// <summary>The files holding the incomplete image.</summary>
    public IReadOnlyList<string> PartialPaths { get; }
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
    /// <summary>
    /// <see cref="MatchesRecorded"/>: the written output, re-read, reproduces the digest computed
    /// while writing. <see cref="SourceMatchesRecorded"/>: the source container's own recorded
    /// acquisition hash reproduced while reading (null when the source recorded none).
    ///
    /// <para>A source container with chunks that could not be decoded yields an output with
    /// zeros where evidence was. That is never a success: both flags are then false whatever
    /// the hashes say, and <see cref="SourceDamagedChunks"/> says how many chunks were lost.
    /// <see cref="DamagedChunks"/> is the total across source and output, so a caller that only
    /// looks at it still sees the damage.</para>
    /// </summary>
    public sealed record ConversionResult(
        ImageJobResult Image,
        string? RecordedMd5,
        string? RecordedSha1,
        int DamagedChunks,
        bool? MatchesRecorded,
        bool? SourceMatchesRecorded = null,
        int SourceDamagedChunks = 0,
        int OutputDamagedChunks = 0);

    /// <summary>
    /// Any supported source (raw, E01, AFF4, VHD, VHDX) to any in-process target. The output
    /// is re-read through the matching reader and must hash back to what was written; a
    /// container source is additionally checked against its recorded acquisition hash.
    /// </summary>
    public static async Task<ConversionResult> ConvertAsync(
        string source, string output, ImageFormat format, string? examiner = null,
        int compressionLevel = 1, long segmentSizeMiB = 1500, bool verify = true,
        IProgress<ImageJobProgress>? progress = null, CancellationToken ct = default, bool overwrite = false)
    {
        if (!InProcessImager.Supports(format))
        {
            throw new NotSupportedException($"{format} cannot be written in-process.");
        }

        string? recordedMd5 = null, recordedSha1 = null;
        var sourceDamaged = 0;
        ImageJobResult result;
        var job = new ImageJob(source, output, format, CompressionLevel: compressionLevel, SegmentSizeMiB: segmentSizeMiB,
            ExaminerName: examiner, Description: $"Converted from {Path.GetFileName(source)}", Overwrite: overwrite);

        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (ext is ".vhd" or ".vhdx")
        {
            using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using DiscUtils.VirtualDisk disk = ext == ".vhd"
                ? new DiscUtils.Vhd.Disk(file, DiscUtils.Streams.Ownership.None)
                : new DiscUtils.Vhdx.Disk(file, DiscUtils.Streams.Ownership.None);
            result = await new InProcessImager().ImageStreamAsync(disk.Content, job, progress, ct).ConfigureAwait(false);
        }
        else if (EvidenceOpener.IsEwf(source))
        {
            using var ewf = EwfReader.Open(source);
            await using var stream = ewf.OpenStream();
            result = await new InProcessImager().ImageStreamAsync(stream, job, progress, ct).ConfigureAwait(false);
            recordedMd5 = ewf.RecordedMd5;
            recordedSha1 = ewf.RecordedSha1;
            sourceDamaged = ewf.DamagedChunks.Count;
        }
        else if (EvidenceOpener.IsAff4(source))
        {
            using var aff4 = Aff4.Aff4Reader.Open(source);
            await using var stream = aff4.OpenStream();
            result = await new InProcessImager().ImageStreamAsync(stream, job, progress, ct).ConfigureAwait(false);
            recordedMd5 = aff4.RecordedMd5;
            recordedSha1 = aff4.RecordedSha1;
            sourceDamaged = aff4.DamagedChunks.Count;
        }
        else
        {
            result = await new InProcessImager().ImageAsync(job, progress, ct).ConfigureAwait(false);
        }
        sourceDamaged = (int)Math.Min(int.MaxValue, Math.Max(sourceDamaged, result.SourceDamagedChunks));

        bool? sourceOk = null;
        if (recordedMd5 is not null && result.Md5 is not null)
        {
            sourceOk = string.Equals(recordedMd5, result.Md5, StringComparison.OrdinalIgnoreCase);
        }
        else if (recordedSha1 is not null && result.Sha1 is not null)
        {
            sourceOk = string.Equals(recordedSha1, result.Sha1, StringComparison.OrdinalIgnoreCase);
        }
        if (sourceDamaged > 0)
        {
            // Zero-filled source chunks are a failed read of the source whether or not it
            // recorded a hash to disagree with.
            sourceOk = false;
        }

        if (!verify)
        {
            return new ConversionResult(result, recordedMd5, recordedSha1, sourceDamaged,
                sourceDamaged > 0 ? false : null, sourceOk, sourceDamaged, 0);
        }

        var (rereadMd5, outputDamaged) = await RereadMd5Async(output, format, ct).ConfigureAwait(false);
        var outputOk = rereadMd5 is null ? (bool?)null
            : string.Equals(rereadMd5, result.Md5, StringComparison.OrdinalIgnoreCase) && outputDamaged == 0;
        if (sourceDamaged > 0)
        {
            // The output faithfully holds what was read, but what was read is not the evidence.
            outputOk = false;
        }
        return new ConversionResult(result, recordedMd5, recordedSha1, sourceDamaged + outputDamaged, outputOk, sourceOk,
            sourceDamaged, outputDamaged);
    }

    private static async Task<(string? Md5, int Damaged)> RereadMd5Async(string path, ImageFormat format, CancellationToken ct)
    {
        using var md5 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
        var buffer = new byte[1 << 20];
        var damaged = 0;

        async Task HashAsync(Stream s)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var n = await s.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) break;
                md5.AppendData(buffer, 0, n);
            }
        }

        switch (format)
        {
            case ImageFormat.Vhd:
            case ImageFormat.Vhdx:
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using DiscUtils.VirtualDisk disk = format == ImageFormat.Vhd
                        ? new DiscUtils.Vhd.Disk(file, DiscUtils.Streams.Ownership.None)
                        : new DiscUtils.Vhdx.Disk(file, DiscUtils.Streams.Ownership.None);
                    await HashAsync(disk.Content).ConfigureAwait(false);
                    break;
                }
            case ImageFormat.Ewf:
                {
                    using var ewf = EwfReader.Open(path);
                    await using var s = ewf.OpenStream();
                    await HashAsync(s).ConfigureAwait(false);
                    damaged = ewf.DamagedChunks.Count;
                    break;
                }
            case ImageFormat.Aff4:
                {
                    using var aff4 = Aff4.Aff4Reader.Open(path);
                    await using var s = aff4.OpenStream();
                    await HashAsync(s).ConfigureAwait(false);
                    damaged = aff4.DamagedChunks.Count;
                    break;
                }
            default:
                {
                    await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                    await HashAsync(s).ConfigureAwait(false);
                    break;
                }
        }
        return (Convert.ToHexStringLower(md5.GetHashAndReset()), damaged);
    }

    public static async Task<ConversionResult> EwfToRawAsync(
        string sourceE01, string outputRaw, string? examiner = null,
        IProgress<ImageJobProgress>? progress = null, CancellationToken ct = default, bool overwrite = false)
    {
        using var ewf = EwfReader.Open(sourceE01);
        await using var stream = ewf.OpenStream();

        var job = new ImageJob(sourceE01, outputRaw, ImageFormat.Raw, ExaminerName: examiner,
            Description: $"Converted from EWF ({ewf.SegmentCount} segment(s))", Overwrite: overwrite);
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
        var damaged = ewf.DamagedChunks.Count;
        if (damaged > 0)
        {
            matches = false;
        }

        return new ConversionResult(result, ewf.RecordedMd5, ewf.RecordedSha1, damaged, matches,
            SourceDamagedChunks: damaged);
    }

    /// <summary>
    /// Raw → E01. <paramref name="verify"/> re-reads the finished chain and hashes it, which
    /// doubles the time and is what turns "written" into "verified"; leave it on unless the
    /// Verify tool will be run separately.
    /// </summary>
    public static async Task<ConversionResult> RawToEwfAsync(
        string sourceRaw, string outputE01, string? examiner = null, int compressionLevel = 1, long segmentSizeMiB = 1500,
        bool verify = true, IProgress<ImageJobProgress>? progress = null, CancellationToken ct = default, bool overwrite = false)
    {
        var job = new ImageJob(sourceRaw, outputE01, ImageFormat.Ewf, CompressionLevel: compressionLevel, SegmentSizeMiB: segmentSizeMiB,
            ExaminerName: examiner, Description: "Converted from raw", Overwrite: overwrite);
        var result = await new InProcessImager().ImageAsync(job, progress, ct).ConfigureAwait(false);
        var sourceDamaged = (int)Math.Min(int.MaxValue, result.SourceDamagedChunks);

        if (!verify)
        {
            return new ConversionResult(result, result.Md5, result.Sha1, sourceDamaged, sourceDamaged > 0 ? false : null,
                SourceDamagedChunks: sourceDamaged);
        }

        using var ewf = EwfReader.Open(outputE01);
        var v = await ewf.VerifyAsync(null, ct).ConfigureAwait(false);
        var outputDamaged = ewf.DamagedChunks.Count;
        var matches = sourceDamaged == 0 && v.Verified && string.Equals(ewf.RecordedMd5, result.Md5, StringComparison.OrdinalIgnoreCase);
        return new ConversionResult(result, ewf.RecordedMd5, ewf.RecordedSha1, sourceDamaged + outputDamaged, matches,
            SourceDamagedChunks: sourceDamaged, OutputDamagedChunks: outputDamaged);
    }
}
