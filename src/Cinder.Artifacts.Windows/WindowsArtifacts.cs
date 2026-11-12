using Cinder.Artifacts;

namespace Cinder.Artifacts.Windows;

// ============== Registry ==============

public sealed record RegistryHive(
    string Path,
    string HiveType,         // "NTUSER" | "SYSTEM" | "SOFTWARE" | "SAM" | "SECURITY" | "AMCACHE" | etc.
    int Version,
    DateTimeOffset? LastWritten,
    long EntryCount);

public sealed record RegistryKeyArtifact(
    string KeyPath,
    DateTimeOffset? LastWritten,
    IReadOnlyList<RegistryValue> Values,
    IReadOnlyList<string> Subkeys) : ArtifactBase(
        Source: "registry.key",
        User: null,
        Timestamp: LastWritten,
        Summary: KeyPath);

public sealed record RegistryValue(string Name, string ValueType, string DataPreview);

public sealed record UserAssistEntry(
    string User,
    string ProgramName,
    int RunCount,
    TimeSpan? FocusTime,
    DateTimeOffset? LastExecuted) : ArtifactBase(
        Source: "registry.userassist",
        User: User,
        Timestamp: LastExecuted,
        Summary: $"{ProgramName} (run {RunCount}×)");

public sealed record ShimCacheEntry(
    string Path,
    DateTimeOffset? Modified,
    bool Executed) : ArtifactBase(
        Source: "registry.shimcache",
        User: null,
        Timestamp: Modified,
        Summary: $"{Path}{(Executed ? " (executed)" : "")}");

public sealed record AmcacheEntry(
    string Path,
    string? Sha1,
    DateTimeOffset? FirstSeen,
    string? Publisher) : ArtifactBase(
        Source: "registry.amcache",
        User: null,
        Timestamp: FirstSeen,
        Summary: $"{Path}");

public sealed record UsbDeviceArtifact(
    string DeviceId,
    string FriendlyName,
    DateTimeOffset? FirstConnected,
    DateTimeOffset? LastConnected,
    string? SerialNumber) : ArtifactBase(
        Source: "registry.usbstor",
        User: null,
        Timestamp: LastConnected,
        Summary: $"{FriendlyName} ({SerialNumber})");

public sealed record WifiNetworkArtifact(
    string Ssid,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    string? Authentication) : ArtifactBase(
        Source: "registry.wifi",
        User: null,
        Timestamp: LastSeen,
        Summary: Ssid);

public sealed record SrumApplicationUsage(
    string User,
    string Application,
    DateTimeOffset? Timestamp,
    long ForegroundCpuMs,
    long BytesRead,
    long BytesWritten) : ArtifactBase(
        Source: "srum.application",
        User: User,
        Timestamp: Timestamp,
        Summary: $"{Application} ({ForegroundCpuMs} ms fg)");

public sealed record SrumNetworkUsage(
    string User,
    string Application,
    DateTimeOffset? Timestamp,
    long BytesSent,
    long BytesReceived,
    string? Interface) : ArtifactBase(
        Source: "srum.network",
        User: User,
        Timestamp: Timestamp,
        Summary: $"{Application}: {BytesSent}↑ / {BytesReceived}↓");

// ============== Prefetch ==============

/// <param name="ExecutableName">Executable name from the file header.</param>
/// <param name="Hash">Path hash from the file header.</param>
/// <param name="RunCount">Run counter from the file header.</param>
/// <param name="LastRun">Most recent run; null when the stored value is unset or not a representable time.</param>
/// <param name="AllRunTimes">Every run time the file records (up to eight), newest first.</param>
/// <param name="LoadedFiles">Files the executable loaded (not parsed yet: always empty).</param>
/// <param name="FormatVersion">Prefetch format version (17, 23, 26, 30, 31); null if the worker did not say.</param>
/// <param name="SourceFile">Name of the .pf file this row came from.</param>
public sealed record PrefetchEntry(
    string ExecutableName,
    string Hash,
    int RunCount,
    DateTimeOffset? LastRun,
    IReadOnlyList<DateTimeOffset> AllRunTimes,
    IReadOnlyList<string> LoadedFiles,
    int? FormatVersion = null,
    string? SourceFile = null) : ArtifactBase(
        Source: "prefetch",
        User: null,
        Timestamp: LastRun,
        Summary: $"{ExecutableName} (run {RunCount}×)");

/// <summary>A .pf file that produced no row, and why.</summary>
public sealed record SkippedPrefetchFile(string File, string Reason);

/// <summary>
/// A Prefetch directory as parsed. <see cref="Skipped"/> matters as much as the rows: on
/// Windows 10 and later every Prefetch file is MAM-compressed, the parser does not decode those
/// yet, and a directory of 200 files that yields no rows is "200 files not decoded", not "no
/// program ever ran".
/// </summary>
/// <param name="Entries">Parsed files.</param>
/// <param name="Skipped">Files that were not parsed, each with its reason.</param>
/// <param name="CompressedNotDecoded">How many of <paramref name="Skipped"/> are MAM-compressed files.</param>
public sealed record PrefetchResult(
    IReadOnlyList<PrefetchEntry> Entries,
    IReadOnlyList<SkippedPrefetchFile> Skipped,
    int CompressedNotDecoded);

// ============== Shellbags ==============

public sealed record ShellbagEntry(
    string User,
    string Path,
    DateTimeOffset? FirstAccessed,
    DateTimeOffset? LastAccessed,
    int AccessCount) : ArtifactBase(
        Source: "shellbags",
        User: User,
        Timestamp: LastAccessed,
        Summary: Path);

// ============== Jumplists / LNK ==============

public sealed record JumplistEntry(
    string User,
    string ApplicationId,
    string TargetPath,
    DateTimeOffset? AccessTime) : ArtifactBase(
        Source: "jumplists",
        User: User,
        Timestamp: AccessTime,
        Summary: TargetPath);

public sealed record LnkEntry(
    string Path,
    string TargetPath,
    string? Arguments,
    string? IconLocation,
    string? WorkingDirectory,
    DateTimeOffset? TargetCreated,
    DateTimeOffset? TargetModified,
    DateTimeOffset? TargetAccessed,
    string? VolumeSerialNumber,
    string? MachineId) : ArtifactBase(
        Source: "lnk",
        User: null,
        Timestamp: TargetAccessed,
        Summary: $"{Path} → {TargetPath}");

// ============== Event Log ==============

public sealed record EventLogRecord(
    long EventRecordId,
    int EventId,
    string Provider,
    string Channel,
    string Computer,
    string? User,
    DateTimeOffset? Timestamp,
    string Level,
    string Summary,
    IReadOnlyDictionary<string, string>? Extras = null) : ArtifactBase(
        Source: "evtx",
        User: User,
        Timestamp: Timestamp,
        Summary: Summary,
        Extras: Extras);

// ============== Browser ==============

/// <param name="User">Profile owner.</param>
/// <param name="Browser">Browser name.</param>
/// <param name="Url">The URL.</param>
/// <param name="Title">Page title.</param>
/// <param name="VisitCount">
/// Visits recorded for the URL. 0 is a real value (a bookmark or typed URL never visited);
/// null means the database held no usable number. Neither is "1".
/// </param>
/// <param name="Timestamp">Last visit; null when never visited or when the stored value is not a representable time.</param>
/// <param name="VisitType">Transition type, when known.</param>
public sealed record BrowserHistoryEntry(
    string User,
    string Browser,
    string Url,
    string? Title,
    int? VisitCount,
    DateTimeOffset? Timestamp,
    string? VisitType) : ArtifactBase(
        Source: "browser.history",
        User: User,
        Timestamp: Timestamp,
        Summary: $"{Url} ({Title})");

/// <summary>A browser history database as parsed, with what could not be read from it.</summary>
/// <param name="Entries">The rows.</param>
/// <param name="RowErrors">Rows that could not be converted at all and are not in <paramref name="Entries"/>.</param>
/// <param name="TimestampErrors">Rows that are in <paramref name="Entries"/> with a null timestamp because the stored value was not a time.</param>
/// <param name="ReadError">Set when the database was damaged part-way: <paramref name="Entries"/> then holds only the rows read before the damage.</param>
public sealed record BrowserHistoryResult(
    IReadOnlyList<BrowserHistoryEntry> Entries,
    int RowErrors,
    int TimestampErrors,
    string? ReadError)
{
    /// <summary>True when every row was read and converted cleanly.</summary>
    public bool IsComplete => RowErrors == 0 && TimestampErrors == 0 && ReadError is null;
}

public sealed record BrowserDownloadEntry(
    string User,
    string Browser,
    string Url,
    string TargetPath,
    long? Size,
    DateTimeOffset? Timestamp,
    string? ReferrerUrl) : ArtifactBase(
        Source: "browser.downloads",
        User: User,
        Timestamp: Timestamp,
        Summary: $"{Url} → {TargetPath}");

public sealed record BrowserCookieEntry(
    string User,
    string Browser,
    string Domain,
    string Name,
    DateTimeOffset? Created,
    DateTimeOffset? Expires) : ArtifactBase(
        Source: "browser.cookies",
        User: User,
        Timestamp: Created,
        Summary: $"{Domain}/{Name}");

// ============== Misc ==============

public sealed record RecycleBinEntry(
    string User,
    string OriginalPath,
    long Size,
    DateTimeOffset DeletedAt) : ArtifactBase(
        Source: "recycle-bin",
        User: User,
        Timestamp: DeletedAt,
        Summary: OriginalPath);

public sealed record RdpCacheEntry(
    string User,
    string TargetHost,
    DateTimeOffset? Timestamp,
    string CachePath) : ArtifactBase(
        Source: "rdp-cache",
        User: User,
        Timestamp: Timestamp,
        Summary: TargetHost);
