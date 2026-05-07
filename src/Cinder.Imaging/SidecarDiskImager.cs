using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Imaging;

/// <summary>
/// <see cref="IDiskImager"/> backed by the Python <c>parsers/imager/imager_worker.py</c> sidecar,
/// which wraps libewf-python (E01), pyaff4 (AFF4), and a plain Python writer (raw .dd / VHD).
/// Spawns one sidecar per job so a crash doesn't poison the next acquisition.
/// </summary>
public sealed class SidecarDiskImager : IDiskImager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<ProcessStartInfo> _sidecarFactory;
    private readonly ILogger<SidecarDiskImager> _logger;

    public SidecarDiskImager(Func<ProcessStartInfo> sidecarFactory, ILogger<SidecarDiskImager>? logger = null)
    {
        _sidecarFactory = sidecarFactory ?? throw new ArgumentNullException(nameof(sidecarFactory));
        _logger = logger ?? NullLogger<SidecarDiskImager>.Instance;
    }

    public static ProcessStartInfo DefaultSidecar(string parsersDir) => new()
    {
        FileName = OperatingSystem.IsWindows() ? "python.exe" : "python3",
        ArgumentList = { "-m", "imager.imager_worker" },
        WorkingDirectory = parsersDir,
    };

    public async Task<ImageJobResult> ImageAsync(ImageJob job, IProgress<ImageJobProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using var sidecar = new SidecarClient(_sidecarFactory(), _logger);
        var start = Stopwatch.GetTimestamp();

        var progressCts = new CancellationTokenSource();
        var pollTask = progress is null ? Task.CompletedTask : Task.Run(async () =>
        {
            while (!progressCts.Token.IsCancellationRequested)
            {
                try
                {
                    var snap = await sidecar.InvokeAsync("progress", null, progressCts.Token).ConfigureAwait(false);
                    if (snap is JsonObject o)
                    {
                        progress.Report(new ImageJobProgress(
                            BytesRead: o["bytes_read"]?.GetValue<long>() ?? 0,
                            TotalBytes: o["total_bytes"]?.GetValue<long?>(),
                            Throughput: o["throughput"]?.GetValue<double>() ?? 0,
                            BadSectors: o["bad_sectors"]?.GetValue<long>() ?? 0,
                            Phase: o["phase"]?.GetValue<string>() ?? "running"));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (SidecarException) { break; }
                await Task.Delay(250, progressCts.Token).ConfigureAwait(false);
            }
        }, progressCts.Token);

        try
        {
            var args = JsonNode.Parse(JsonSerializer.Serialize(job, JsonOptions));
            var result = await sidecar.InvokeAsync("image", args, ct).ConfigureAwait(false);
            await progressCts.CancelAsync();
            await pollTask.ConfigureAwait(false);

            if (result is not JsonObject ro)
            {
                throw new SidecarException("Imager returned no result.");
            }

            return new ImageJobResult(
                OutputPath: ro["output_path"]?.GetValue<string>() ?? job.OutputPath,
                BytesWritten: ro["bytes_written"]?.GetValue<long>() ?? 0,
                Md5: ro["md5"]?.GetValue<string?>(),
                Sha1: ro["sha1"]?.GetValue<string?>(),
                Sha256: ro["sha256"]?.GetValue<string?>(),
                Blake3: ro["blake3"]?.GetValue<string?>(),
                BadSectors: ro["bad_sectors"]?.GetValue<long>() ?? 0,
                Elapsed: Stopwatch.GetElapsedTime(start));
        }
        finally
        {
            progressCts.Cancel();
        }
    }
}
