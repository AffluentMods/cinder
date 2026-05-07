using Cinder.Artifacts;

namespace Cinder.Mobile;

public sealed record MobileMessage(
    string Source,                  // "iphone.imessage" | "android.sms" | "whatsapp" | "signal"
    string? User,
    string ChatId,
    string? Sender,
    string? Recipient,
    string Body,
    DateTimeOffset? Timestamp,
    bool FromMe) : ArtifactBase(Source, User, Timestamp, $"{Sender ?? "?"} → {Recipient ?? "?"}: {Body}");

public sealed record MobileCall(
    string Source,
    string? User,
    string Direction,
    string Number,
    string? Contact,
    DateTimeOffset? Timestamp,
    TimeSpan Duration) : ArtifactBase(Source, User, Timestamp, $"{Direction} {Contact ?? Number} ({Duration})");

public sealed record MobileApp(
    string Source,
    string? User,
    string PackageOrBundle,
    string DisplayName,
    string? Version,
    DateTimeOffset? Installed,
    DateTimeOffset? LastUsed) : ArtifactBase(Source, User, Installed ?? LastUsed, DisplayName);

public interface IMobileBackupReader
{
    Task<MobileBackupInfo> InspectAsync(string backupPath, CancellationToken ct = default);
    IAsyncEnumerable<MobileMessage> MessagesAsync(string backupPath, CancellationToken ct = default);
    IAsyncEnumerable<MobileCall> CallsAsync(string backupPath, CancellationToken ct = default);
    IAsyncEnumerable<MobileApp> AppsAsync(string backupPath, CancellationToken ct = default);
}

public sealed record MobileBackupInfo(string Platform, string DeviceName, string? Os, DateTimeOffset? Created, bool Encrypted);
