using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Cinder.App.Services;
using Cinder.App.ViewModels.Tools;
using Cinder.Core.Custody;
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

    /// <summary>
    /// ATT&amp;CK technique or tactic id prefix (<c>T1078</c>, <c>T1053</c>, <c>TA0002</c>).
    /// Matches by prefix so <c>T1053</c> catches <c>T1053.005</c>.
    /// </summary>
    [ObservableProperty]
    private string? _mitreFilter;

    [ObservableProperty]
    private string _statusLine = "0 events.";

    /// <summary>Ids the tagger can emit, for the filter's suggestion list.</summary>
    public IReadOnlyList<string> KnownMitreIds { get; } =
        MitreTagger.KnownIds.OrderBy(id => id, StringComparer.Ordinal).Select(MitreTagger.Describe).ToArray();

    private TimelineFilter CurrentFilter() => new(
        User: string.IsNullOrWhiteSpace(UserFilter) ? null : UserFilter,
        Sources: string.IsNullOrWhiteSpace(SourceFilter) ? null : [SourceFilter],
        TextContains: string.IsNullOrWhiteSpace(TextFilter) ? null : TextFilter,
        MitreTechnique: NormaliseMitre(MitreFilter));

    /// <summary>Accepts "T1078", "T1078 Valid Accounts" (from the picker), or whitespace.</summary>
    private static string? NormaliseMitre(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var first = raw.Trim().Split(' ', 2)[0];
        return first.Length == 0 ? null : first;
    }

    [RelayCommand]
    private void Refresh()
    {
        Events.Clear();
        Histogram.Clear();
        var filter = CurrentFilter();
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

    /// <summary>
    /// Writes every event matching the current filter — not just the 5,000 the grid shows — in
    /// one of the ecosystem's interchange formats: Timesketch JSONL, Timesketch CSV, or the
    /// Sleuth Kit body format that <c>mactime</c>, Autopsy and Plaso read.
    /// </summary>
    [RelayCommand]
    private async Task ExportAsync(string format, CancellationToken ct)
    {
        var (ext, label) = format switch
        {
            "jsonl" => ("jsonl", "Timesketch JSONL"),
            "body" => ("body", "bodyfile (mactime)"),
            _ => ("csv", "Timesketch CSV"),
        };

        var path = await ToolDialog.SaveFileAsync($"Export timeline as {label}", $"timeline.{ext}", ext);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        StatusLine = $"Exporting {label}…";
        try
        {
            var filter = CurrentFilter();
            var events = Timeline.Range(From, To, filter).ToList();

            var written = await Task.Run(() =>
            {
                using var w = new StreamWriter(path, append: false, System.Text.Encoding.UTF8);
                return format switch
                {
                    "jsonl" => TimelineExporter.WriteTimesketchJsonl(events, w),
                    "body" => TimelineExporter.WriteBodyfile(events, w),
                    _ => TimelineExporter.WriteTimesketchCsv(events, w),
                };
            }, ct);

            StatusLine = $"Exported {written:N0} events → {path}";
            await ActiveCaseContext.LogAsync(CustodyAction.DataExported, new
            {
                Tool = "timeline",
                Format = label,
                Path = path,
                Events = written,
                From,
                To,
                Filter = new { filter.User, filter.TextContains, Source = SourceFilter, Mitre = filter.MitreTechnique },
            }, ct);
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Export cancelled.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task IngestFolderAsync(CancellationToken ct)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick a triage folder to ingest",
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        StatusLine = "Ingesting…";
        var progress = new Progress<string>(s => StatusLine = $"Ingesting · {s}");
        try
        {
            var stats = await TimelineIngester.IngestAsync(Timeline, path, progress, ct);
            // From / To windows track widest range so all events show
            if (Timeline.Count > 0)
            {
                var window = Timeline.Range(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToList();
                if (window.Count > 0)
                {
                    From = window[0].Timestamp.AddDays(-1);
                    To = window[^1].Timestamp.AddDays(1);
                }
            }
            Refresh();
            StatusLine = $"Ingested: {stats}. {Timeline.Count:N0} events on the timeline.";
            await ActiveCaseContext.LogAsync(CustodyAction.ParserRan, new
            {
                Tool = "timeline.ingest",
                Folder = path,
                Events = Timeline.Count,
                Stats = stats.ToString(),
            }, ct);
        }
        catch (Exception ex)
        {
            StatusLine = $"Ingest failed: {ex.Message}";
        }
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
