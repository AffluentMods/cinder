using Cinder.Artifacts;

namespace Cinder.Search;

/// <summary>
/// In-memory super-timeline. Aggregates artifacts from every parser into a single sorted axis
/// the UI can virtually render. For 1M+ events the timeline auto-buckets into time slices for
/// rendering performance.
/// </summary>
public sealed class SuperTimeline
{
    private readonly List<TimelineEvent> _events = new();

    public int Count => _events.Count;

    public void Add(IArtifact artifact, IReadOnlyList<string>? tags = null)
    {
        if (artifact.Timestamp is not { } ts)
        {
            return;
        }
        _events.Add(new TimelineEvent(
            Timestamp: ts,
            Source: artifact.Source,
            User: artifact.User,
            Summary: artifact.Summary,
            Tags: tags ?? []));
    }

    public void Sort() => _events.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

    public IEnumerable<TimelineEvent> Range(DateTimeOffset from, DateTimeOffset to,
        TimelineFilter? filter = null)
    {
        // Binary search would be better, but for first cut a linear pass is fine — UI calls Range
        // once per pan/zoom, on a sorted list, with cancellation.
        foreach (var e in _events)
        {
            if (e.Timestamp < from) continue;
            if (e.Timestamp > to) break;
            if (filter is null || filter.Matches(e))
            {
                yield return e;
            }
        }
    }

    /// <summary>Bucket events into N equal-width time slices, returning per-bucket counts. The
    /// timeline UI uses this to draw a histogram strip above the detail axis.</summary>
    public IReadOnlyList<int> Histogram(DateTimeOffset from, DateTimeOffset to, int buckets,
        TimelineFilter? filter = null)
    {
        if (buckets <= 0 || from >= to)
        {
            return [];
        }
        var counts = new int[buckets];
        var totalTicks = (to - from).Ticks;
        if (totalTicks <= 0)
        {
            return counts;
        }
        foreach (var e in Range(from, to, filter))
        {
            var idx = (int)(((e.Timestamp - from).Ticks * (long)buckets) / totalTicks);
            if (idx == buckets) idx = buckets - 1;
            counts[idx]++;
        }
        return counts;
    }
}

public sealed record TimelineEvent(
    DateTimeOffset Timestamp,
    string Source,
    string? User,
    string Summary,
    IReadOnlyList<string> Tags);

public sealed record TimelineFilter(
    string? User = null,
    IReadOnlyCollection<string>? Sources = null,
    string? TextContains = null,
    IReadOnlyCollection<string>? Tags = null,
    string? MitreTechnique = null)
{
    public bool Matches(TimelineEvent e)
    {
        if (User is not null && !string.Equals(User, e.User, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (Sources is { Count: > 0 } && !Sources.Contains(e.Source))
        {
            return false;
        }
        if (TextContains is { Length: > 0 } && !e.Summary.Contains(TextContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (Tags is { Count: > 0 } && !Tags.All(t => e.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (MitreTechnique is { Length: > 0 } &&
            !e.Tags.Any(t => t.StartsWith(MitreTechnique, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        return true;
    }
}
