using Cinder.Artifacts;

namespace Cinder.Artifacts.Linux;

public sealed record ShellHistoryEntry(string User, string Shell, string Command, DateTimeOffset? Timestamp)
    : ArtifactBase("linux.shell-history", User, Timestamp, $"{Shell}: {Command}");

public sealed record AuthLogEntry(DateTimeOffset? Timestamp, string Host, string Process, string Message, string? User, string? RemoteHost)
    : ArtifactBase("linux.auth-log", User, Timestamp, $"{Process}: {Message}");

public sealed record JournalctlEntry(DateTimeOffset? Timestamp, string Unit, string Priority, string Message, string? User)
    : ArtifactBase("linux.journalctl", User, Timestamp, $"{Unit}: {Message}");

public sealed record SyslogEntry(DateTimeOffset? Timestamp, string Host, string Process, string Message)
    : ArtifactBase("linux.syslog", null, Timestamp, $"{Process}: {Message}");

public sealed record CronEntry(string User, string Schedule, string Command, string Source)
    : ArtifactBase("linux.cron", User, null, $"{Schedule} → {Command}");

public sealed record AtJob(string User, DateTimeOffset? Scheduled, string Command)
    : ArtifactBase("linux.at", User, Scheduled, Command);

public sealed record SshKnownHost(string User, string Host, string KeyType, string Fingerprint)
    : ArtifactBase("linux.ssh.known_hosts", User, null, $"{Host} ({KeyType})");

public sealed record SshAuthorizedKey(string User, string KeyType, string Comment, string Fingerprint)
    : ArtifactBase("linux.ssh.authorized_keys", User, null, $"{KeyType} {Comment}");

public sealed record TrashEntry(string User, string OriginalPath, long Size, DateTimeOffset? DeletedAt)
    : ArtifactBase("linux.trash", User, DeletedAt, OriginalPath);

public sealed record RecentlyUsedEntry(string User, string Uri, string? MimeType, DateTimeOffset? Modified, DateTimeOffset? Visited)
    : ArtifactBase("linux.recently-used", User, Visited ?? Modified, Uri);

public sealed record SystemdUnit(string Name, string Path, bool Enabled, bool Masked, string? UnitFileState)
    : ArtifactBase("linux.systemd", null, null, $"{Name} ({UnitFileState ?? "?"})");

public sealed record PackageLogEntry(DateTimeOffset? Timestamp, string PackageManager, string Action, string Package, string? Version)
    : ArtifactBase($"linux.{PackageManager}", null, Timestamp, $"{Action} {Package} {Version}");

public sealed record UserAccount(string Name, int Uid, int Gid, string HomeDirectory, string Shell, string? Comment, string? PasswordHash)
    : ArtifactBase("linux.passwd", Name, null, $"{Name} (uid={Uid})");
