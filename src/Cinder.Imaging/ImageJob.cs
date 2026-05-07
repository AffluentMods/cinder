namespace Cinder.Imaging;

/// <summary>Configuration for a disk-imaging job. Passed to the imager sidecar verbatim.</summary>
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
    int ReadErrorRetries = 2);

public sealed record ImageJobProgress(
    long BytesRead,
    long? TotalBytes,
    double Throughput,        // bytes/sec
    long BadSectors,
    string Phase);            // "reading" | "hashing" | "writing" | "verifying" | "done"

public sealed record ImageJobResult(
    string OutputPath,
    long BytesWritten,
    string? Md5,
    string? Sha1,
    string? Sha256,
    string? Blake3,
    long BadSectors,
    TimeSpan Elapsed);
