using Cinder.Artifacts;
using Cinder.Sidecar;

namespace Cinder.Artifacts.Linux;

// Timestamps on these records.
//
// A Linux log does not always say what it means by a time. An epoch-based value (shell history,
// the journal) is an instant. A classic syslog line ("Mar  3 04:05:06") records neither a year
// nor a zone; apt's history and a Trash info file record local time and no zone. For those the
// record's timestamp holds the wall-clock reading exactly as written, with a placeholder offset
// of +00:00, and TimeZoneKnown is false: it is NOT a UTC instant. Show it as "local time, zone
// not recorded", never with a "Z" or "UTC" suffix, and do not order it against real instants
// as if it were one. The same fact is in Extras["time_basis"] = "local-unknown-zone" for code
// that only sees IArtifact (the key and value the filesystem walker uses for FAT volumes).
//
// A timestamp the parser could not determine is null. It is never a minimum date.

internal static class LinuxTimeBasis
{
    /// <summary>Extras describing how a record's timestamp is to be read; null when it is a plain instant.</summary>
    public static IReadOnlyDictionary<string, string>? Extras(DateTimeOffset? timestamp, bool zoneKnown, bool yearInferred = false)
    {
        if (timestamp is null || (zoneKnown && !yearInferred))
        {
            return null;
        }
        var extras = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!zoneKnown)
        {
            extras[SidecarJson.TimeBasisKey] = SidecarJson.TimeBasisLocalUnknownZone;
        }
        if (yearInferred)
        {
            extras["year_inferred"] = "true";
        }
        return extras;
    }
}

public sealed record ShellHistoryEntry(string User, string Shell, string Command, DateTimeOffset? Timestamp)
    : ArtifactBase("linux.shell-history", User, Timestamp, $"{Shell}: {Command}");

/// <summary>One auth.log / secure line.</summary>
/// <param name="Timestamp">See the note at the top of this file; null when the line's time could not be parsed.</param>
/// <param name="Host">Logging host.</param>
/// <param name="Process">Logging process.</param>
/// <param name="Message">Message text.</param>
/// <param name="User">Account the message names, if any.</param>
/// <param name="RemoteHost">Peer address the message names, if any.</param>
/// <param name="TimeZoneKnown">False when <paramref name="Timestamp"/> is a wall-clock reading in an unrecorded zone.</param>
/// <param name="YearInferred">True when the line carried no year and the parser inferred it from when the log was last written.</param>
/// <param name="TimestampRaw">The timestamp text exactly as it appears in the log.</param>
public sealed record AuthLogEntry(
    DateTimeOffset? Timestamp, string Host, string Process, string Message, string? User, string? RemoteHost,
    bool TimeZoneKnown = true, bool YearInferred = false, string? TimestampRaw = null)
    : ArtifactBase("linux.auth-log", User, Timestamp, $"{Process}: {Message}",
        LinuxTimeBasis.Extras(Timestamp, TimeZoneKnown, YearInferred));

public sealed record JournalctlEntry(DateTimeOffset? Timestamp, string Unit, string Priority, string Message, string? User)
    : ArtifactBase("linux.journalctl", User, Timestamp, $"{Unit}: {Message}");

public sealed record SyslogEntry(DateTimeOffset? Timestamp, string Host, string Process, string Message)
    : ArtifactBase("linux.syslog", null, Timestamp, $"{Process}: {Message}");

public sealed record CronEntry(string User, string Schedule, string Command, string Source)
    : ArtifactBase("linux.cron", User, null, $"{Schedule} → {Command} ({Source})");

public sealed record AtJob(string User, DateTimeOffset? Scheduled, string Command)
    : ArtifactBase("linux.at", User, Scheduled, Command);

/// <param name="User">Owner of the known_hosts file.</param>
/// <param name="Host">Host pattern (possibly hashed).</param>
/// <param name="KeyType">Key algorithm.</param>
/// <param name="Fingerprint">OpenSSH SHA256 fingerprint of the key; null when the key could not be decoded.</param>
/// <param name="Marker"><c>@cert-authority</c> / <c>@revoked</c>, if the line has one.</param>
public sealed record SshKnownHost(string User, string Host, string KeyType, string? Fingerprint, string? Marker = null)
    : ArtifactBase("linux.ssh.known_hosts", User, null, $"{Host} ({KeyType})");

public sealed record SshAuthorizedKey(string User, string KeyType, string Comment, string? Fingerprint)
    : ArtifactBase("linux.ssh.authorized_keys", User, null, $"{KeyType} {Comment}");

/// <param name="User">Owner of the Trash directory.</param>
/// <param name="OriginalPath">Where the item was deleted from.</param>
/// <param name="Size">Size of the trashed item; null when it is no longer in the Trash (not 0).</param>
/// <param name="DeletedAt">See the note at the top of this file: the spec stores local time, no zone.</param>
/// <param name="TimeZoneKnown">False when <paramref name="DeletedAt"/> is a wall-clock reading in an unrecorded zone.</param>
public sealed record TrashEntry(string User, string OriginalPath, long? Size, DateTimeOffset? DeletedAt, bool TimeZoneKnown = true)
    : ArtifactBase("linux.trash", User, DeletedAt, OriginalPath, LinuxTimeBasis.Extras(DeletedAt, TimeZoneKnown));

public sealed record RecentlyUsedEntry(string User, string Uri, string? MimeType, DateTimeOffset? Modified, DateTimeOffset? Visited)
    : ArtifactBase("linux.recently-used", User, Visited ?? Modified, Uri);

public sealed record SystemdUnit(string Name, string Path, bool Enabled, bool Masked, string? UnitFileState)
    : ArtifactBase("linux.systemd", null, null, $"{Name} ({UnitFileState ?? "?"})");

/// <param name="Timestamp">See the note at the top of this file: apt logs local time, pacman logs an offset.</param>
/// <param name="PackageManager">apt / dnf / pacman.</param>
/// <param name="Action">install / upgrade / remove / ...</param>
/// <param name="Package">Package name.</param>
/// <param name="Version">Version installed, removed, or upgraded to.</param>
/// <param name="TimeZoneKnown">False when <paramref name="Timestamp"/> is a wall-clock reading in an unrecorded zone.</param>
/// <param name="PreviousVersion">For an upgrade or downgrade, the version replaced.</param>
public sealed record PackageLogEntry(
    DateTimeOffset? Timestamp, string PackageManager, string Action, string Package, string? Version,
    bool TimeZoneKnown = true, string? PreviousVersion = null)
    : ArtifactBase($"linux.{PackageManager}", null, Timestamp, $"{Action} {Package} {Version}",
        LinuxTimeBasis.Extras(Timestamp, TimeZoneKnown));

public sealed record UserAccount(string Name, int Uid, int Gid, string HomeDirectory, string Shell, string? Comment, string? PasswordHash)
    : ArtifactBase("linux.passwd", Name, null, $"{Name} (uid={Uid})");

/// <summary>A path the parser would not read, and why (a symlink leaving the evidence root, a device node, ...).</summary>
public sealed record RefusedPath(string Path, string Reason);

/// <summary>
/// What one Linux parser call produced: the rows, and everything the parser declined or could
/// not finish. A listing with a non-empty <see cref="Refused"/> or <see cref="Truncated"/> is
/// not the whole picture, and whoever shows the rows has to say so.
/// </summary>
/// <param name="Entries">The parsed rows.</param>
/// <param name="Refused">Paths that were not read.</param>
/// <param name="Truncated">Files that were longer than the parser's read limit; their tail was not parsed.</param>
public sealed record LinuxArtifactResult<T>(
    IReadOnlyList<T> Entries,
    IReadOnlyList<RefusedPath> Refused,
    IReadOnlyList<string> Truncated)
{
    /// <summary>Lines that were neither blank, comment nor parseable (cron, ssh key files, passwd).</summary>
    public int UnparsedLines { get; init; }

    /// <summary>Journal only: which cap ended the read early (<c>entries</c>, <c>bytes</c>, <c>timeout</c>), or null.</summary>
    public string? TruncatedBy { get; init; }

    /// <summary>auth.log only: where inferred years came from (<c>file_mtime</c> or <c>reference_mtime</c>).</summary>
    public string? YearSource { get; init; }

    /// <summary>auth.log only: the instant years were inferred from.</summary>
    public DateTimeOffset? YearReference { get; init; }

    /// <summary>Anything else the parser had to say (a missing <c>journalctl</c> binary, its stderr).</summary>
    public string? Note { get; init; }

    /// <summary>True when nothing was refused, truncated or left unparsed.</summary>
    public bool IsComplete => Refused.Count == 0 && Truncated.Count == 0 && TruncatedBy is null && UnparsedLines == 0;
}
