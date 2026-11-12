using System.Diagnostics;
using System.Text.Json.Nodes;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Imaging;

/// <summary>What a verification run established about an image.</summary>
public enum ImageVerificationStatus
{
    /// <summary>The image was re-hashed and every recorded digest matched.</summary>
    Verified,

    /// <summary>The image was re-hashed and a recorded digest did not match: the image is not what was acquired.</summary>
    Mismatch,

    /// <summary>
    /// The image was re-hashed but there is no recorded digest to compare with (no
    /// <c>.sha256</c> / <c>.md5</c> companion). Nothing is known about its integrity: this is
    /// neither a pass nor a failure, and must not be shown as either.
    /// </summary>
    NoReference,
}

/// <summary>
/// The full outcome of <see cref="ImageVerifier.VerifyDetailedAsync"/>. Unlike
/// <see cref="VerificationResult"/>, whose single boolean cannot tell "the digests differ" from
/// "there was nothing to compare against", this keeps the three cases apart and says why.
/// </summary>
/// <param name="Status">Verified, mismatch, or no reference.</param>
/// <param name="Reason">Why the status is not <see cref="ImageVerificationStatus.Verified"/>; null when it is.</param>
/// <param name="ExpectedSha256">Recorded SHA-256, if any.</param>
/// <param name="ActualSha256">SHA-256 of the image as it is now.</param>
/// <param name="ExpectedMd5">Recorded MD5, if any.</param>
/// <param name="ActualMd5">MD5 of the image as it is now.</param>
/// <param name="BytesVerified">Bytes hashed.</param>
public sealed record ImageVerificationReport(
    ImageVerificationStatus Status,
    string? Reason,
    string? ExpectedSha256,
    string? ActualSha256,
    string? ExpectedMd5,
    string? ActualMd5,
    long BytesVerified)
{
    /// <summary>True: verified. False: mismatch. Null: no reference digest, so unknown.</summary>
    public bool? Match => Status switch
    {
        ImageVerificationStatus.Verified => true,
        ImageVerificationStatus.Mismatch => false,
        _ => null,
    };

    /// <summary>
    /// The two-state view. <see cref="VerificationResult.Match"/> is true only for
    /// <see cref="ImageVerificationStatus.Verified"/>; "no reference" becomes false there
    /// because that type has no way to say "unknown", and an unknown must never read as a pass.
    /// </summary>
    public VerificationResult ToResult() => new(
        Match: Status == ImageVerificationStatus.Verified,
        ExpectedSha256, ActualSha256, ExpectedMd5, ActualMd5, BytesVerified);
}

/// <summary>
/// Image verification: re-hash an existing image (raw / E01 / AFF4) and compare against the
/// hashes recorded inside the container or alongside it (`.sha256` companion file).
/// </summary>
public sealed class ImageVerifier : IImageVerifier
{
    private readonly Func<ISidecarChannel> _open;
    private readonly ILogger<ImageVerifier> _logger;

    public ImageVerifier(Func<ProcessStartInfo> sidecarFactory, ILogger<ImageVerifier>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sidecarFactory);
        _logger = logger ?? NullLogger<ImageVerifier>.Instance;
        _open = () => new SidecarClient(sidecarFactory(), _logger);
    }

    /// <summary>Uses an already-built channel factory (tests, or a host that pools sidecars).</summary>
    public ImageVerifier(Func<ISidecarChannel> channelFactory, ILogger<ImageVerifier>? logger = null)
    {
        _open = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _logger = logger ?? NullLogger<ImageVerifier>.Instance;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="VerificationResult.Match"/> is false both for a mismatch and when no
    /// reference digest exists. Use <see cref="VerifyDetailedAsync"/> to tell them apart.
    /// </remarks>
    public async Task<VerificationResult> VerifyAsync(string imagePath, IProgress<long>? progress = null, CancellationToken ct = default)
        => (await VerifyDetailedAsync(imagePath, progress, ct).ConfigureAwait(false)).ToResult();

    /// <summary>Re-hashes an image and reports whether it is verified, mismatched, or has no reference digest.</summary>
    public async Task<ImageVerificationReport> VerifyDetailedAsync(string imagePath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await using var sidecar = _open();
        var args = new JsonObject { ["image_path"] = imagePath };
        var relay = progress is null ? null : new BytesVerifiedRelay(progress);
        var response = await sidecar.InvokeAsync("verify", args, relay, null, ct).ConfigureAwait(false);
        if (response is not JsonObject o)
        {
            throw new SidecarException("Verifier returned no result.");
        }
        var report = ToReport(o);
        progress?.Report(report.BytesVerified);
        if (report.Status != ImageVerificationStatus.Verified)
        {
            _logger.LogWarning("Verification of {Image}: {Status}. {Reason}", imagePath, report.Status, report.Reason);
        }
        return report;
    }

    /// <summary>
    /// Maps a <c>verify</c> reply. The status is decided here from the digests themselves, not
    /// taken from the worker's <c>match</c> flag alone: no recorded digest is "no reference"
    /// whatever the flag says (an older worker answered <c>match: true</c> in that case), and a
    /// claimed match whose digests differ is a mismatch.
    /// </summary>
    private static ImageVerificationReport ToReport(JsonObject o)
    {
        var expectedSha = Digest(o["expected_sha256"]);
        var actualSha = Digest(o["actual_sha256"]);
        var expectedMd5 = Digest(o["expected_md5"]);
        var actualMd5 = Digest(o["actual_md5"]);
        var claimed = SidecarJson.Boolean(o["match"]);
        var workerReason = SidecarJson.String(o["reason"]);

        ImageVerificationStatus status;
        string? reason;
        if (expectedSha is null && expectedMd5 is null)
        {
            status = ImageVerificationStatus.NoReference;
            reason = workerReason ?? "No reference hash: neither a .sha256 nor a .md5 companion file exists for this image.";
        }
        else
        {
            var shaOk = expectedSha is null || string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase);
            var md5Ok = expectedMd5 is null || string.Equals(expectedMd5, actualMd5, StringComparison.OrdinalIgnoreCase);
            if (shaOk && md5Ok && claimed != false)
            {
                status = ImageVerificationStatus.Verified;
                reason = null;
            }
            else
            {
                status = ImageVerificationStatus.Mismatch;
                reason = workerReason
                    ?? (!shaOk ? "The SHA-256 of the image differs from the recorded digest."
                        : !md5Ok ? "The MD5 of the image differs from the recorded digest."
                        : "The verifier reported a mismatch.");
            }
        }

        return new ImageVerificationReport(status, reason, expectedSha, actualSha, expectedMd5, actualMd5,
            BytesVerified: SidecarJson.Int64(o["bytes_verified"]) ?? 0);
    }

    private static string? Digest(JsonNode? node)
    {
        var text = SidecarJson.String(node)?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Turns the worker's <c>$/progress</c> notifications into a byte count.</summary>
    private sealed class BytesVerifiedRelay(IProgress<long> target) : IProgress<JsonObject>
    {
        public void Report(JsonObject value)
        {
            if (SidecarJson.Int64(value["bytes_verified"]) is { } bytes)
            {
                target.Report(bytes);
            }
        }
    }
}
