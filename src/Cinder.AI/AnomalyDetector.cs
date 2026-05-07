using Cinder.Artifacts;

namespace Cinder.AI;

/// <summary>
/// Local statistical anomaly detection — runs without any LLM. Flags timeline events that fall
/// far outside the per-user activity profile (off-hours sessions, sudden bursts, rare sources).
/// Phase 9.1 will add a small ONNX model trained on real DFIR cases for shape-based anomaly.
/// </summary>
public sealed class AnomalyDetector
{
    public IEnumerable<Anomaly> Detect(IReadOnlyList<IArtifact> events)
    {
        if (events.Count == 0)
        {
            yield break;
        }
        var byUser = events.Where(e => e.User is not null && e.Timestamp.HasValue)
            .GroupBy(e => e.User!, StringComparer.OrdinalIgnoreCase);

        foreach (var grp in byUser)
        {
            var hours = grp.Select(e => e.Timestamp!.Value.UtcDateTime.Hour).ToArray();
            var workdayHours = hours.Count(h => h is >= 8 and <= 19);
            var offHours = hours.Length - workdayHours;
            if (hours.Length >= 10 && offHours > workdayHours)
            {
                yield return new Anomaly(
                    grp.Key,
                    "off-hours-skew",
                    $"User {grp.Key} has {offHours}/{hours.Length} events outside 08:00–19:00 UTC.",
                    grp.Min(e => e.Timestamp!.Value),
                    grp.Max(e => e.Timestamp!.Value));
            }

            // Burst detection: more than 3× the median per-day count is suspicious.
            var perDay = grp.GroupBy(e => e.Timestamp!.Value.Date).Select(g => (Date: g.Key, Count: g.Count())).ToList();
            if (perDay.Count >= 5)
            {
                var ordered = perDay.Select(p => p.Count).OrderBy(c => c).ToList();
                var median = ordered[ordered.Count / 2];
                foreach (var d in perDay)
                {
                    if (median > 0 && d.Count > median * 3 && d.Count >= 25)
                    {
                        yield return new Anomaly(
                            grp.Key, "activity-burst",
                            $"User {grp.Key} on {d.Date:yyyy-MM-dd}: {d.Count} events vs median {median}/day.",
                            d.Date, d.Date.AddDays(1));
                    }
                }
            }
        }

        // Cross-user rare-source check: sources used by a single user only.
        var sourceUsers = events.Where(e => e.User is not null)
            .GroupBy(e => e.Source)
            .Select(g => (Source: g.Key, Users: g.Select(x => x.User!).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                          User: g.First().User!, Sample: g.First()))
            .Where(x => x.Users == 1 && events.Count > 50);

        foreach (var s in sourceUsers)
        {
            yield return new Anomaly(
                s.User, "single-user-source",
                $"Source {s.Source} only ever observed for user {s.User}.",
                s.Sample.Timestamp ?? DateTimeOffset.MinValue,
                s.Sample.Timestamp ?? DateTimeOffset.MinValue);
        }
    }
}

public sealed record Anomaly(string User, string Kind, string Message, DateTimeOffset From, DateTimeOffset To);
