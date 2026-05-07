namespace Cinder.Core.Hashing;

/// <summary>Lowercase hex digests for the algorithms requested. Null if not requested.</summary>
public sealed record MultiHashResult(
    long BytesHashed,
    string? Md5,
    string? Sha1,
    string? Sha256,
    string? Blake3);
