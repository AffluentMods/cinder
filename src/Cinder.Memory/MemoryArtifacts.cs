using Cinder.Artifacts;

namespace Cinder.Memory;

public sealed record MemoryProcess(
    int Pid,
    int ParentPid,
    string ImageName,
    string? CommandLine,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExitedAt,
    int Threads,
    int Handles,
    string? SessionId,
    string? IntegrityLevel,
    bool Suspicious,
    IReadOnlyList<string>? Anomalies = null) : ArtifactBase(
        Source: "memory.process",
        User: null,
        Timestamp: CreatedAt,
        Summary: $"{ImageName} pid={Pid}");

public sealed record MemoryConnection(
    int Pid,
    string Protocol,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    string State,
    DateTimeOffset? CreatedAt) : ArtifactBase(
        Source: "memory.netscan",
        User: null,
        Timestamp: CreatedAt,
        Summary: $"{Protocol} {LocalAddress}:{LocalPort} → {RemoteAddress}:{RemotePort} ({State})");

public sealed record LoadedModule(
    int Pid,
    string ModuleName,
    string Path,
    string? BaseAddress,
    long Size,
    bool IsSigned) : ArtifactBase(
        Source: "memory.modules",
        User: null,
        Timestamp: null,
        Summary: $"{ModuleName} (pid={Pid})");

public sealed record InjectionFinding(
    int Pid,
    string ImageName,
    string Type,         // "malfind" | "hollowfind" | "ldrmodules"
    string? Address,
    long Length,
    string? Notes) : ArtifactBase(
        Source: "memory.injection",
        User: null,
        Timestamp: null,
        Summary: $"{Type}: {ImageName} pid={Pid}");

public sealed record CredentialDump(
    string Source,        // "hashdump" | "lsadump" | "cachedump" | "mimikatz"
    string Account,
    string? Domain,
    string? Hash,
    DateTimeOffset? LastChange) : ArtifactBase(
        Source: $"memory.{Source}",
        User: Account,
        Timestamp: LastChange,
        Summary: $"{Source}: {Domain}\\{Account}");
