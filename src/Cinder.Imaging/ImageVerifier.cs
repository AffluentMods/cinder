using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Imaging;

/// <summary>
/// Image verification: re-hash an existing image (raw / E01 / AFF4) and compare against the
/// hashes recorded inside the container or alongside it (`.sha256` companion file).
/// </summary>
public sealed class ImageVerifier : IImageVerifier
{
    private readonly Func<ProcessStartInfo> _sidecarFactory;
    private readonly ILogger<ImageVerifier> _logger;

    public ImageVerifier(Func<ProcessStartInfo> sidecarFactory, ILogger<ImageVerifier>? logger = null)
    {
        _sidecarFactory = sidecarFactory ?? throw new ArgumentNullException(nameof(sidecarFactory));
        _logger = logger ?? NullLogger<ImageVerifier>.Instance;
    }

    public async Task<VerificationResult> VerifyAsync(string imagePath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await using var sidecar = new SidecarClient(_sidecarFactory(), _logger);
        var args = new JsonObject { ["image_path"] = imagePath };
        var response = await sidecar.InvokeAsync("verify", args, ct).ConfigureAwait(false);
        if (response is not JsonObject o)
        {
            throw new SidecarException("Verifier returned no result.");
        }
        progress?.Report(o["bytes_verified"]?.GetValue<long>() ?? 0);
        return new VerificationResult(
            Match: o["match"]?.GetValue<bool>() ?? false,
            ExpectedSha256: o["expected_sha256"]?.GetValue<string?>(),
            ActualSha256: o["actual_sha256"]?.GetValue<string?>(),
            ExpectedMd5: o["expected_md5"]?.GetValue<string?>(),
            ActualMd5: o["actual_md5"]?.GetValue<string?>(),
            BytesVerified: o["bytes_verified"]?.GetValue<long>() ?? 0);
    }
}
