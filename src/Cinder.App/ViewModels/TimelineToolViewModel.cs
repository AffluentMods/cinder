using System.Collections.ObjectModel;
using Cinder.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

/// <summary>UI wrapper for <see cref="SuperTimeline"/>. Sources are pluggable; for v0.1 we
/// ship the wrapper and let parsers feed into it. Demo seed adds a few fake events on demand
/// so the histogram + range UI is testable without a real case loaded.</summary>
public sealed partial class TimelineToolViewModel : ViewModelBase
{
    public SuperTimeline Timeline { get; } = new();

    public ObservableCollection<TimelineEvent> Events { get; } = new();
    public ObservableCollection<int> Histogram { get; } = new();

    [ObservableProperty]
    private DateTimeOffset _from = DateTimeOffset.UtcNow.AddYears(-1);

    [ObservableProperty]
    private DateTimeOffset _to = DateTimeOffset.UtcNow;

    [ObservableProperty]
    private string? _userFilter;

    [ObservableProperty]
    private string? _textFilter;

    [ObservableProperty]
    private string? _sourceFilter;

    [ObservableProperty]
    private string _statusLine = "0 events.";

    [RelayCommand]
    private void Refresh()
    {
        Events.Clear();
        Histogram.Clear();
        var filter = new TimelineFilter(
            User: UserFilter,
            Sources: string.IsNullOrEmpty(SourceFilter) ? null : [SourceFilter],
            TextContains: TextFilter);
        foreach (var e in Timeline.Range(From, To, filter))
        {
            Events.Add(e);
            if (Events.Count >= 5000)
            {
                break;
            }
        }
        var hist = Timeline.Histogram(From, To, 64, filter);
        foreach (var h in hist) Histogram.Add(h);
        StatusLine = $"{Events.Count:N0} events shown · {Timeline.Count:N0} indexed.";
    }

    [RelayCommand]
    private void AddDemoEvents()
    {
        // Until a case feeds artifacts in, this seeds the timeline with synthetic events so the
        // histogram + range UI can be tested without real evidence.
        var rng = new Random(42);
        for (int i = 0; i < 250; i++)
        {
            var ts = DateTimeOffset.UtcNow.AddDays(-rng.Next(0, 365)).AddHours(-rng.Next(0, 23));
            var src = (rng.Next(5)) switch
            {
                0 => "evtx",
                1 => "browser.history",
                2 => "registry.userassist",
                3 => "memory.process",
                _ => "linux.auth-log",
            };
            Timeline.Add(new SyntheticArtifact(src, $"user{rng.Next(1, 4)}", ts, $"Event #{i} from {src}"));
        }
        Timeline.Sort();
        Refresh();
    }

    private sealed record SyntheticArtifact(string Source, string? User, DateTimeOffset? Timestamp, string Summary)
        : Cinder.Artifacts.ArtifactBase(Source, User, Timestamp, Summary);
}
