using Cinder.Artifacts;

namespace Cinder.Memory;

// Numeric fields on these records are nullable on purpose. Volatility reports a field it could
// not read from the image (paged out, smeared, overwritten) as an "absent value", and the
// worker passes that on as null. Null means "not recovered". It is not 0: a process with a
// null handle count has an unknown number of handles, and PID 0 is a real process (Idle).

public sealed record MemoryProcess(
    int? Pid,
    int? ParentPid,
    string ImageName,
    string? CommandLine,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExitedAt,
    int? Threads,
    int? Handles,
    string? SessionId,
    string? IntegrityLevel,
    bool Suspicious,
    IReadOnlyList<string>? Anomalies = null) : ArtifactBase(
        Source: "memory.process",
        User: null,
        Timestamp: CreatedAt,
        Summary: $"{ImageName} pid={MemoryText.OrUnknown(Pid)}");

public sealed record MemoryConnection(
    int? Pid,
    string Protocol,
    string LocalAddress,
    int? LocalPort,
    string RemoteAddress,
    int? RemotePort,
    string State,
    DateTimeOffset? CreatedAt) : ArtifactBase(
        Source: "memory.netscan",
        User: null,
        Timestamp: CreatedAt,
        Summary: $"{Protocol} {LocalAddress}:{MemoryText.OrUnknown(LocalPort)} → {RemoteAddress}:{MemoryText.OrUnknown(RemotePort)} ({State})");

public sealed record LoadedModule(
    int? Pid,
    string ModuleName,
    string Path,
    string? BaseAddress,
    long? Size,
    bool IsSigned) : ArtifactBase(
        Source: "memory.modules",
        User: null,
        Timestamp: null,
        Summary: $"{ModuleName} (pid={MemoryText.OrUnknown(Pid)})");

/// <param name="Pid">Process the region belongs to.</param>
/// <param name="ImageName">Process image name.</param>
/// <param name="Type">"malfind" | "hollowfind" | "ldrmodules".</param>
/// <param name="Address">Start of the region.</param>
/// <param name="Length">Length of the region; null when not recovered.</param>
/// <param name="Notes">For malfind: the first bytes of the region, as lowercase hex.</param>
public sealed record InjectionFinding(
    int? Pid,
    string ImageName,
    string Type,
    string? Address,
    long? Length,
    string? Notes) : ArtifactBase(
        Source: "memory.injection",
        User: null,
        Timestamp: null,
        Summary: $"{Type}: {ImageName} pid={MemoryText.OrUnknown(Pid)}");

/// <param name="Source">"hashdump" | "lsadump" | "cachedump" | "mimikatz".</param>
/// <param name="Account">Account name.</param>
/// <param name="Domain">Domain, when the source records one.</param>
/// <param name="Hash">pwdump-style <c>LM:NT</c>; null when neither hash was recovered (never the empty pair ":").</param>
/// <param name="LastChange">Password last set, when the source records it.</param>
/// <param name="Rid">Relative identifier of the account.</param>
/// <param name="LmHash">LM hash alone; null when not recovered.</param>
/// <param name="NtHash">NT hash alone; null when not recovered.</param>
public sealed record CredentialDump(
    string Source,
    string Account,
    string? Domain,
    string? Hash,
    DateTimeOffset? LastChange,
    int? Rid = null,
    string? LmHash = null,
    string? NtHash = null) : ArtifactBase(
        Source: $"memory.{Source}",
        User: Account,
        Timestamp: LastChange,
        Summary: $"{Source}: {Domain}\\{Account}");

internal static class MemoryText
{
    /// <summary>A number for a summary line, or "?" when it was not recovered.</summary>
    public static string OrUnknown(int? value)
        => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
}
