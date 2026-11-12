using Cinder.Artifacts;
using Cinder.Search;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// <see cref="SuperTimeline.Histogram"/> over windows no real case should have but hostile
/// evidence can: a single artefact with a far-future (or zeroed) timestamp stretches the
/// timeline to centuries, and the bucket index used to overflow.
/// </summary>
public sealed class SuperTimelineHistogramTests
{
    private sealed record Event(string Source, string? User, DateTimeOffset? Timestamp, string Summary)
        : ArtifactBase(Source, User, Timestamp, Summary);

    private static SuperTimeline TimelineOf(params DateTimeOffset[] timestamps)
    {
        var timeline = new SuperTimeline();
        foreach (var ts in timestamps)
        {
            timeline.Add(new Event("test", null, ts, "event"));
        }
        timeline.Sort();
        return timeline;
    }

    [Fact]
    public void A_far_future_timestamp_does_not_overflow_the_bucket_index()
    {
        // What the app does after an ingest: window = first event - 1 day .. last event + 1 day,
        // 64 buckets. A recycle-bin $I record dated 2500 next to ordinary 2024 activity makes
        // that window 476 years: ticks * 64 exceeds Int64.MaxValue for the late event.
        var ordinary = new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var planted = new DateTimeOffset(2500, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeline = TimelineOf(ordinary, ordinary.AddHours(1), planted);
        var from = ordinary.AddDays(-1);
        var to = planted.AddDays(1);

        var histogram = timeline.Histogram(from, to, 64);

        histogram.Should().HaveCount(64);
        histogram.Sum().Should().Be(3, "every event in range is counted exactly once");
        histogram[0].Should().Be(2);
        histogram[63].Should().Be(1, "the far-future event belongs in the last bucket");
    }

    [Fact]
    public void The_widest_possible_window_is_handled()
    {
        var timeline = TimelineOf(
            DateTimeOffset.MinValue,
            new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.MaxValue);

        var histogram = timeline.Histogram(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 1000);

        histogram.Should().HaveCount(1000);
        histogram.Sum().Should().Be(4);
        histogram[0].Should().Be(1);
        histogram[999].Should().Be(1);
    }

    [Fact]
    public void Events_fall_in_the_bucket_their_position_in_the_window_implies()
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeline = TimelineOf(
            start,                        // bucket 0
            start.AddHours(5.5),          // bucket 5
            start.AddHours(9.999),        // bucket 9
            start.AddHours(10));          // the closing edge: last bucket, not one past it

        var histogram = timeline.Histogram(start, start.AddHours(10), 10);

        histogram.Should().Equal(1, 0, 0, 0, 0, 1, 0, 0, 0, 2);
    }
}
