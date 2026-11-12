namespace Cinder.Imaging;

/// <summary>
/// Configuration for a disk-imaging job. Passed to the imager sidecar verbatim.
///
/// <para><c>Overwrite</c> is false by default: the in-process imager then refuses to start,
/// with <see cref="OutputExistsException"/>, when the output already exists — the image itself,
/// its <c>.partial</c> working file, or for E01 any segment of the target chain — and touches
/// nothing. True replaces the existing image; for E01 the segments of the old image that the new
/// one does not reuse are removed when the new image is complete.</para>
/// </summary>
public sealed record ImageJob(
    string SourceDevice,
    string OutputPath,
    ImageFormat Format,
    int CompressionLevel = 1,
    long SegmentSizeMiB = 2048,
    string? CaseNumber = null,
    string? EvidenceNumber = null,
    string? ExaminerName = null,
    string? Description = null,
    string? Notes = null,
    bool ComputeMd5 = true,
    bool ComputeSha1 = true,
    bool ComputeSha256 = true,
    bool ComputeBlake3 = false,
    bool ReadErrorRetry = true,
    int ReadErrorRetries = 2,
    bool Overwrite = false);

public sealed record ImageJobProgress(
    long BytesRead,
    long? TotalBytes,
    double Throughput,        // bytes/sec
    long BadSectors,
    string Phase);            // "reading" | "hashing" | "writing" | "verifying" | "done"

/// <summary>Outcome of an acquisition.</summary>
/// <param name="OutputPath">The finished image (first segment for a segmented format).</param>
/// <param name="BytesWritten">Bytes acquired from the source, bad sectors included; excludes <paramref name="PaddingBytes"/>.</param>
/// <param name="Md5">Digest of the media the image holds, including any sector padding.</param>
/// <param name="Sha1">As <paramref name="Md5"/>.</param>
/// <param name="Sha256">As <paramref name="Md5"/>.</param>
/// <param name="Blake3">As <paramref name="Md5"/>; sidecar only.</param>
/// <param name="BadSectors">Sectors of the source that could not be read and were zero-filled.</param>
/// <param name="Elapsed">Wall-clock duration.</param>
/// <param name="SourceDamagedChunks">
/// When the source was itself a container (E01, AFF4): chunks it could not decode and
/// zero-filled. Not bad sectors of this run, but the image holds filler where they were, and an
/// image with any is not a faithful copy of the original evidence.
/// </param>
/// <param name="PaddingBytes">
/// Zero bytes appended to bring the media up to a whole sector for formats that store whole
/// sectors (EWF, VHD, VHDX). The digests cover them.
/// </param>
public sealed record ImageJobResult(
    string OutputPath,
    long BytesWritten,
    string? Md5,
    string? Sha1,
    string? Sha256,
    string? Blake3,
    long BadSectors,
    TimeSpan Elapsed,
    long SourceDamagedChunks = 0,
    long PaddingBytes = 0);
