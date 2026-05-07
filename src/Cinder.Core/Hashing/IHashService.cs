namespace Cinder.Core.Hashing;

/// <summary>Computes one or more hashes over a stream in a single pass.</summary>
public interface IHashService
{
    Task<MultiHashResult> ComputeAsync(
        Stream input,
        IReadOnlyCollection<HashAlgorithmKind> algorithms,
        IProgress<long>? progress = null,
        CancellationToken ct = default);

    Task<MultiHashResult> ComputeFileAsync(
        string path,
        IReadOnlyCollection<HashAlgorithmKind> algorithms,
        IProgress<long>? progress = null,
        CancellationToken ct = default);
}
