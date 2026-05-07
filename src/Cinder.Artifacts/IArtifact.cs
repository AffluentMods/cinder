namespace Cinder.Artifacts;

/// <summary>
/// Common shape for every parsed artifact in Cinder. Each parser emits records that share these
/// fields so the super-timeline (Phase 6) and report builder (Phase 8) can treat them uniformly.
/// Parser-specific extras live in <see cref="Extras"/>.
/// </summary>
public interface IArtifact
{
    string Source { get; }            // e.g. "registry.userassist", "prefetch", "evtx"
    string? User { get; }
    DateTimeOffset? Timestamp { get; }
    string Summary { get; }
    IReadOnlyDictionary<string, string>? Extras { get; }
}

public abstract record ArtifactBase(
    string Source,
    string? User,
    DateTimeOffset? Timestamp,
    string Summary,
    IReadOnlyDictionary<string, string>? Extras = null) : IArtifact;
