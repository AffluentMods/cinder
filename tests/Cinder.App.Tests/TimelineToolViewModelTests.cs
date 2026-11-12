using Cinder.App.ViewModels;
using Cinder.Search;
using FluentAssertions;
using Xunit;

namespace Cinder.App.Tests;

/// <summary>
/// The super-timeline view-model: filters apply when applied, export writes what the grid's
/// filter matches, and an ingest builds a new timeline instead of changing the one on screen.
/// </summary>
public class TimelineToolViewModelTests : IDisposable
{
    private sealed record Event(string Source, string? User, DateTimeOffset? Timestamp, string Summary)
        : Cinder.Artifacts.ArtifactBase(Source, User, Timestamp, Summary);

    private static readonly DateTimeOffset Day = new(2024, 6, 10, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-tests-" + Guid.NewGuid().ToString("N"));

    public TimelineToolViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static TimelineToolViewModel WithEvents()
    {
        var vm = new TimelineToolViewModel();
        vm.Timeline.Add(new Event("evtx.Security", "alice", Day.AddHours(1), "An account was successfully logged on"));
        vm.Timeline.Add(new Event("evtx.System", "SYSTEM", Day.AddHours(2), "Service installed"));
        vm.Timeline.Add(new Event("browser.history", "alice", Day.AddHours(3), "Visited https://example.com/login"));
        vm.Timeline.Add(new Event("prefetch", null, Day.AddHours(23), "Executed: TOOL.EXE"));
        vm.Timeline.Sort();
        vm.From = Day.AddDays(-1);
        vm.To = Day.AddDays(2);
        vm.RefreshCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void Export_writes_the_filter_the_grid_is_showing_not_what_has_been_typed_since()
    {
        var vm = WithEvents();
        vm.TextFilter = "logged on";
        vm.RefreshCommand.Execute(null);
        vm.Events.Should().ContainSingle();
        vm.FiltersEdited.Should().BeFalse();

        // Typed, but not applied: the grid still shows the "logged on" filter.
        vm.TextFilter = "Executed";
        vm.SourceFilter = "prefetch";

        vm.FiltersEdited.Should().BeTrue();
        vm.Events.Should().ContainSingle().Which.Summary.Should().Contain("logged on");
        vm.EventsForExport().Should().Equal(vm.Events, "the export must hold exactly what the grid's filter matches");

        vm.RefreshCommand.Execute(null);
        vm.Events.Should().ContainSingle().Which.Source.Should().Be("prefetch");
        vm.EventsForExport().Should().Equal(vm.Events);
    }

    [Fact]
    public void Export_holds_every_match_even_when_the_grid_shows_only_the_first_part()
    {
        var vm = new TimelineToolViewModel();
        var total = TimelineToolViewModel.MaxShown + 25;
        for (var i = 0; i < total; i++)
        {
            vm.Timeline.Add(new Event("usn", null, Day.AddSeconds(i), $"record {i}"));
        }
        vm.Timeline.Sort();
        vm.From = Day.AddDays(-1);
        vm.To = Day.AddDays(1);
        vm.RefreshCommand.Execute(null);

        vm.Events.Should().HaveCount(TimelineToolViewModel.MaxShown);
        vm.EventsForExport().Should().HaveCount(total);
        vm.StatusLine.Should().Contain($"first {TimelineToolViewModel.MaxShown:N0} of {total:N0}").And.Contain("Export writes all");
        vm.Histogram.Should().HaveCount(64).And.OnlyContain(h => h >= 0 && h <= 44);
    }

    [Theory]
    [InlineData("evtx", 2)]                 // sources are named evtx.Security, evtx.System
    [InlineData("EVTX.security", 1)]
    [InlineData("evtx, prefetch", 3)]
    [InlineData("browser ; prefetch", 2)]
    [InlineData("registry", 0)]
    [InlineData("", 4)]
    [InlineData(null, 4)]
    public void Source_filter_matches_part_of_a_source_name_and_takes_a_list(string? filter, int expected)
    {
        var vm = WithEvents();
        vm.SourceFilter = filter;
        vm.RefreshCommand.Execute(null);

        vm.Events.Should().HaveCount(expected);
        vm.ShowNoMatches.Should().Be(expected == 0);
        vm.ShowEmptyState.Should().BeFalse("the timeline has events; it is the filter that matches none");
    }

    [Fact]
    public void A_to_date_picked_in_the_calendar_includes_that_whole_day()
    {
        var vm = WithEvents();
        vm.From = Day;
        vm.To = Day;            // the date picker hands back midnight at the start of the day
        vm.RefreshCommand.Execute(null);

        vm.Events.Should().HaveCount(4, "the 23:00 event is on the chosen day");
        TimelineToolViewModel.InclusiveEnd(Day.AddHours(5)).Should().Be(Day.AddHours(5), "a time of day is taken as given");
    }

    [Theory]
    [InlineData(0, 0, 0, "No events yet.")]
    [InlineData(0, 0, 40, "No events match the dates and filters. The timeline holds 40 events.")]
    [InlineData(40, 40, 40, "Showing all 40 events.")]
    [InlineData(7, 7, 40, "Showing 7 of 40 events.")]
    public void Status_line_says_what_is_shown(int shown, int matched, int total, string expected) =>
        TimelineToolViewModel.DescribeShown(shown, matched, total).Should().Be(expected);

    [Fact]
    public void User_and_technique_filters_still_apply()
    {
        var vm = WithEvents();
        vm.UserFilter = "ALICE";
        vm.RefreshCommand.Execute(null);
        vm.Events.Should().HaveCount(2);
    }

    [Fact]
    public async Task Ingest_builds_a_new_timeline_and_leaves_the_one_on_screen_untouched_Async()
    {
        var existing = new SuperTimeline();
        existing.Add(new Event("evtx.Security", "alice", Day, "logon"), ["T1078"]);
        existing.Add(new Event("prefetch", null, Day.AddHours(1), "Executed: A.EXE"), []);
        existing.Sort();

        var result = await TimelineToolViewModel.IngestIntoCopyAsync(existing, _dir, progress: null, CancellationToken.None);

        result.Timeline.Should().NotBeSameAs(existing);
        existing.Count.Should().Be(2, "the timeline being displayed is never modified by an ingest");
        result.CountBefore.Should().Be(2);
        var copied = result.Timeline.Range(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToList();
        copied.Should().HaveCount(2, "the empty folder adds nothing");
        copied[0].Tags.Should().Equal("T1078");
        copied[1].Tags.Should().BeEmpty("events are carried over with the tags they had, not tagged again");
        result.Earliest.Should().Be(Day);
        result.Latest.Should().Be(Day.AddHours(1));
    }

    [Fact]
    public async Task A_stopped_ingest_returns_nothing_and_changes_nothing_Async()
    {
        var existing = new SuperTimeline();
        existing.Add(new Event("prefetch", null, Day, "Executed: A.EXE"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => TimelineToolViewModel.IngestIntoCopyAsync(existing, _dir, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        existing.Count.Should().Be(1);
    }

    [Fact]
    public void Clearing_empties_the_timeline_and_shows_the_empty_state()
    {
        var vm = WithEvents();
        vm.ClearCommand.Execute(null);

        vm.Events.Should().BeEmpty();
        vm.HasEvents.Should().BeFalse();
        vm.ShowEmptyState.Should().BeTrue();
        vm.ShowNoMatches.Should().BeFalse();
    }
}
