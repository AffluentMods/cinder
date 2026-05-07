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

public sealed record PrefetchEntry(
    string ExecutableName,
    string Hash,
    int RunCount,
    DateTimeOffset? LastRun,
    IReadOnlyList<DateTimeOffset> AllRunTimes,
    IReadOnlyList<string> LoadedFiles) : ArtifactBase(
        Source: "prefetch",
        User: null,
        Timestamp: LastRun,
        Summary: $"{ExecutableName} (run {RunCount}×)");

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

public sealed record BrowserHistoryEntry(
    string User,
    string Browser,
    string Url,
    string? Title,
    int VisitCount,
    DateTimeOffset? Timestamp,
    string? VisitType) : ArtifactBase(
        Source: "browser.history",
        User: User,
        Timestamp: Timestamp,
        Summary: $"{Url} ({Title})");

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
