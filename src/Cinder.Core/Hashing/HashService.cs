using System.Buffers;
using System.Security.Cryptography;
using Blake3;

namespace Cinder.Core.Hashing;

/// <summary>
/// Streaming multi-hasher. Reads the input once, fans bytes out to every requested algorithm,
/// returns lowercase hex digests. Designed for evidence-scale inputs — never buffers the whole
/// stream in memory.
/// </summary>
public sealed class HashService : IHashService
{
    private const int DefaultBufferSize = 1 << 20; // 1 MiB

    public async Task<MultiHashResult> ComputeAsync(
        Stream input,
        IReadOnlyCollection<HashAlgorithmKind> algorithms,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(algorithms);
        if (algorithms.Count == 0)
        {
            throw new ArgumentException("At least one algorithm must be requested.", nameof(algorithms));
        }

        var useMd5 = algorithms.Contains(HashAlgorithmKind.Md5);
        var useSha1 = algorithms.Contains(HashAlgorithmKind.Sha1);
        var useSha256 = algorithms.Contains(HashAlgorithmKind.Sha256);
        var useBlake3 = algorithms.Contains(HashAlgorithmKind.Blake3);

        using var md5 = useMd5 ? MD5.Create() : null;
        using var sha1 = useSha1 ? SHA1.Create() : null;
        using var sha256 = useSha256 ? SHA256.Create() : null;
        Hasher blake3 = useBlake3 ? Hasher.New() : default;

        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(DefaultBufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer.AsMemory(0, DefaultBufferSize), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                md5?.TransformBlock(buffer, 0, read, null, 0);
                sha1?.TransformBlock(buffer, 0, read, null, 0);
                sha256?.TransformBlock(buffer, 0, read, null, 0);
                if (useBlake3)
                {
                    blake3.UpdateWithJoin(buffer.AsSpan(0, read));
                }

                total += read;
                progress?.Report(total);
            }

            md5?.TransformFinalBlock([], 0, 0);
            sha1?.TransformFinalBlock([], 0, 0);
            sha256?.TransformFinalBlock([], 0, 0);

            return new MultiHashResult(
                BytesHashed: total,
                Md5: md5 is null ? null : Convert.ToHexStringLower(md5.Hash!),
                Sha1: sha1 is null ? null : Convert.ToHexStringLower(sha1.Hash!),
                Sha256: sha256 is null ? null : Convert.ToHexStringLower(sha256.Hash!),
                Blake3: useBlake3 ? Convert.ToHexStringLower(blake3.Finalize().AsSpan()) : null);
        }
        finally
        {
            pool.Return(buffer);
            if (useBlake3)
            {
                blake3.Dispose();
            }
        }
    }

    public async Task<MultiHashResult> ComputeFileAsync(
        string path,
        IReadOnlyCollection<HashAlgorithmKind> algorithms,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        await using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeAsync(fs, algorithms, progress, ct).ConfigureAwait(false);
    }
}
