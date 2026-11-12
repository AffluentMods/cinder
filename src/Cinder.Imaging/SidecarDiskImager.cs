using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cinder.Core.Diagnostics;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Imaging;

/// <summary>A run of source bytes that could not be read and is zero-filled in the image.</summary>
/// <param name="Offset">Byte offset in the source (and in the image).</param>
/// <param name="Length">Length in bytes.</param>
public readonly record struct UnreadableRange(long Offset, long Length);

/// <summary>
/// Everything the imager sidecar reported about an acquisition. <see cref="Result"/> is the
/// <see cref="IDiskImager"/> view; the rest is what that type has no room for and an examiner
/// still has to be told.
/// </summary>
/// <param name="Result">Output path, byte count, digests, bad-sector count.</param>
/// <param name="BadRanges">Where the zero-filled sectors are (adjacent sectors merged).</param>
/// <param name="BadRangesTruncated">True when there were more ranges than the worker lists; <see cref="ImageJobResult.BadSectors"/> is still the full count.</param>
/// <param name="Complete">
/// False when the source ended before its declared size: the image is shorter than the device
/// claimed to be. Not the same as bad sectors, which keep the image at full length.
/// </param>
/// <param name="SourceSize">Size of the source, when it could be determined.</param>
/// <param name="SectorSize">Sector size used when re-reading failed chunks.</param>
/// <param name="DigestFiles">Companion digest files written next to the image (<c>.sha256</c>, <c>.md5</c>).</param>
public sealed record SidecarImageReport(
    ImageJobResult Result,
    IReadOnlyList<UnreadableRange> BadRanges,
    bool BadRangesTruncated,
    bool Complete,
    long? SourceSize,
    int? SectorSize,
    IReadOnlyList<string> DigestFiles)
{
    /// <summary>
    /// True when the image holds every byte of the source: nothing unreadable, nothing missing.
    /// When false, the digests in <see cref="Result"/> are digests of the image as written
    /// (zero fill included), not of the source.
    /// </summary>
    public bool IsFaithfulCopy => Complete && Result.BadSectors == 0;
}

/// <summary>
/// <see cref="IDiskImager"/> backed by the Python <c>parsers/imager/imager_worker.py</c> sidecar,
/// which wraps libewf-python (E01) and a plain Python writer (raw .dd).
/// Spawns one sidecar per job so a crash doesn't poison the next acquisition.
/// <para>
/// Progress comes from the <c>$/progress</c> notifications the worker emits about once a second
/// while it images. (The worker handles one request at a time, so asking it for progress with a
/// second request could only ever be answered after the image was finished.)
/// </para>
/// </summary>
public sealed class SidecarDiskImager : IDiskImager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<ISidecarChannel> _open;
    private readonly ILogger<SidecarDiskImager> _logger;

    public SidecarDiskImager(Func<ProcessStartInfo> sidecarFactory, ILogger<SidecarDiskImager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sidecarFactory);
        _logger = logger ?? NullLogger<SidecarDiskImager>.Instance;
        _open = () => new SidecarClient(sidecarFactory(), _logger);
    }

    /// <summary>Uses an already-built channel factory (tests, or a host that pools sidecars).</summary>
    public SidecarDiskImager(Func<ISidecarChannel> channelFactory, ILogger<SidecarDiskImager>? logger = null)
    {
        _open = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _logger = logger ?? NullLogger<SidecarDiskImager>.Instance;
    }

    public static ProcessStartInfo DefaultSidecar(string parsersDir) => new()
    {
        FileName = ExecutableResolver.ResolveRequired(
            OperatingSystem.IsWindows() ? "python.exe" : "python3",
            "Install Python 3.12+ and make sure it is on PATH."),
        ArgumentList = { "-m", "imager.imager_worker" },
        WorkingDirectory = parsersDir,
    };

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <see cref="ImageJobResult"/> part of the report. Where the unreadable ranges
    /// are, and whether the source ended early, is only in <see cref="ImageDetailedAsync"/>; here
    /// both are logged as warnings.
    /// </remarks>
    public async Task<ImageJobResult> ImageAsync(ImageJob job, IProgress<ImageJobProgress>? progress = null, CancellationToken ct = default)
        => (await ImageDetailedAsync(job, progress, ct).ConfigureAwait(false)).Result;

    /// <summary>Runs an acquisition and returns the worker's full report.</summary>
    public async Task<SidecarImageReport> ImageDetailedAsync(ImageJob job, IProgress<ImageJobProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using var sidecar = _open();
        var start = Stopwatch.GetTimestamp();

        var args = JsonNode.Parse(JsonSerializer.Serialize(job, JsonOptions));
        var relay = progress is null ? null : new ProgressRelay(progress);
        var result = await sidecar.InvokeAsync("image", args, relay, null, ct).ConfigureAwait(false);
        if (result is not JsonObject ro)
        {
            throw new SidecarException("Imager returned no result.");
        }

        var report = ToReport(ro, job, Stopwatch.GetElapsedTime(start));
        progress?.Report(new ImageJobProgress(
            BytesRead: report.Result.BytesWritten,
            TotalBytes: report.SourceSize,
            Throughput: report.Result.Elapsed > TimeSpan.Zero ? report.Result.BytesWritten / report.Result.Elapsed.TotalSeconds : 0,
            BadSectors: report.Result.BadSectors,
            Phase: "done"));

        if (!report.Complete)
        {
            _logger.LogWarning(
                "Acquisition of {Source} is incomplete: the source ended after {Written} of {Size} bytes.",
                job.SourceDevice, report.Result.BytesWritten, report.SourceSize);
        }
        if (report.Result.BadSectors > 0)
        {
            _logger.LogWarning(
                "Acquisition of {Source}: {BadSectors} unreadable sector(s) in {Ranges} range(s) were zero-filled; " +
                "the digests are of the image as written, not of the source.",
                job.SourceDevice, report.Result.BadSectors, report.BadRanges.Count);
        }
        return report;
    }

    /// <summary>
    /// Maps an <c>image</c> reply. The byte count and the bad-sector count have no safe default:
    /// a reply without them is rejected rather than reported as "0 bytes" or, worse, "0 bad
    /// sectors".
    /// </summary>
    private static SidecarImageReport ToReport(JsonObject ro, ImageJob job, TimeSpan elapsed)
    {
        var bytesWritten = SidecarJson.Int64(ro["bytes_written"])
            ?? throw new SidecarException("Imager result has no usable 'bytes_written'.");
        var badSectors = SidecarJson.Int64(ro["bad_sectors"])
            ?? throw new SidecarException("Imager result has no usable 'bad_sectors'.");

        var ranges = new List<UnreadableRange>();
        if (ro["bad_ranges"] is JsonArray rawRanges)
        {
            foreach (var item in rawRanges)
            {
                if (item is JsonArray { Count: 2 } pair
                    && SidecarJson.Int64(pair[0]) is { } offset && SidecarJson.Int64(pair[1]) is { } length)
                {
                    ranges.Add(new UnreadableRange(offset, length));
                }
            }
        }

        var sourceSize = SidecarJson.Int64(ro["source_size"]);
        // Complete only if the worker says so and the byte count agrees with it.
        var complete = (SidecarJson.Boolean(ro["complete"]) ?? true) && (sourceSize is null || bytesWritten >= sourceSize);

        var jobResult = new ImageJobResult(
            OutputPath: SidecarJson.String(ro["output_path"]) ?? job.OutputPath,
            BytesWritten: bytesWritten,
            Md5: SidecarJson.String(ro["md5"]),
            Sha1: SidecarJson.String(ro["sha1"]),
            Sha256: SidecarJson.String(ro["sha256"]),
            Blake3: SidecarJson.String(ro["blake3"]),
            BadSectors: badSectors,
            Elapsed: elapsed);

        return new SidecarImageReport(
            jobResult,
            ranges,
            BadRangesTruncated: SidecarJson.Boolean(ro["bad_ranges_truncated"]) ?? false,
            Complete: complete,
            SourceSize: sourceSize,
            SectorSize: SidecarJson.Int32(ro["sector_size"]),
            DigestFiles: SidecarJson.Strings(ro["digest_files"]));
    }

    /// <summary>Turns the worker's <c>$/progress</c> notifications into <see cref="ImageJobProgress"/>.</summary>
    private sealed class ProgressRelay(IProgress<ImageJobProgress> target) : IProgress<JsonObject>
    {
        public void Report(JsonObject value)
        {
            if (SidecarJson.Int64(value["bytes_read"]) is not { } bytesRead)
            {
                return; // a keep-alive without figures
            }
            target.Report(new ImageJobProgress(
                BytesRead: bytesRead,
                TotalBytes: SidecarJson.Int64(value["total_bytes"]),
                Throughput: SidecarJson.Double(value["throughput"]) ?? 0,
                BadSectors: SidecarJson.Int64(value["bad_sectors"]) ?? 0,
                Phase: SidecarJson.String(value["phase"]) ?? "imaging"));
        }
    }
}
