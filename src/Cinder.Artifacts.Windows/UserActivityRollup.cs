using Cinder.Artifacts;

namespace Cinder.Artifacts.Windows;

/// <summary>
/// Aggregator that flattens LNK + Jumplists + RecentDocs + browser history + UserAssist into a
/// single per-user timeline. Phase 5 extends this with Linux artifacts.
/// </summary>
public sealed class UserActivityRollup
{
    private readonly List<IArtifact> _events = new();

    public void Add(IArtifact artifact)
    {
        if (artifact.Timestamp.HasValue)
        {
            _events.Add(artifact);
        }
    }

    public void AddRange(IEnumerable<IArtifact> artifacts)
    {
        foreach (var a in artifacts) Add(a);
    }

    public IEnumerable<IArtifact> ForUser(string user, DateTimeOffset from, DateTimeOffset to)
        => _events
            .Where(e => string.Equals(e.User, user, StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Timestamp >= from && e.Timestamp <= to)
            .OrderBy(e => e.Timestamp);

    public IEnumerable<string> Users
        => _events.Where(e => e.User is not null).Select(e => e.User!).Distinct(StringComparer.OrdinalIgnoreCase);

    public int Count => _events.Count;
}
