namespace Cinder.Core.Cases;

/// <summary>A forensic case — the unit of work in Cinder. Backed by a single SQLite file.</summary>
public sealed record Case(
    Guid Id,
    string Name,
    string Examiner,
    string? Description,
    DateTimeOffset CreatedUtc,
    int SchemaVersion);
