namespace Cinder.Imaging;

public interface IDiskImager
{
    Task<ImageJobResult> ImageAsync(
        ImageJob job,
        IProgress<ImageJobProgress>? progress = null,
        CancellationToken ct = default);
}

public interface IImageVerifier
{
    /// <summary>Re-hash an existing image and compare against the recorded hashes.</summary>
    Task<VerificationResult> VerifyAsync(string imagePath, IProgress<long>? progress = null, CancellationToken ct = default);
}

public sealed record VerificationResult(
    bool Match,
    string? ExpectedSha256,
    string? ActualSha256,
    string? ExpectedMd5,
    string? ActualMd5,
    long BytesVerified);
