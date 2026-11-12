using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Cinder.App.Services;
using Cinder.App.ViewModels.Tools;
using Cinder.Core.Custody;
using Cinder.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

/// <summary>
/// The super-timeline: every timestamped event read from one or more evidence folders, in time
/// order, with date, user, source, text and ATT&amp;CK filters.
///
/// <para>Two rules keep what is on screen and what is written to disk honest:</para>
/// <list type="bullet">
/// <item>An ingest never changes the timeline the grid is reading. It builds a new one on the
/// thread pool (the existing events plus the new folder's) and the finished timeline replaces
/// the old one on the UI thread. While it runs, Refresh and Export are disabled.</item>
/// <item>Filters take effect when applied (Refresh, or Enter in a filter box). Export writes the
/// events matching the filter that was last applied, which is the filter the grid is showing,
/// not whatever has been typed since.</item>
/// </list>
/// </summary>
public sealed partial class TimelineToolViewModel : ViewModelBase
{
    /// <summary>Events shown in the grid. The status line says so when more match.</summary>
    internal const int MaxShown = 5_000;

    private const int HistogramBuckets = 64;
    private const int HistogramHeight = 44;

    /// <summary>The current timeline. Replaced, never modified in place, once events are in it.</summary>
    public SuperTimeline Timeline { get; private set; } = new();

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

    /// <summary>One or more source names or parts of names, separated by commas: <c>evtx, browser</c>.</summary>
    [ObservableProperty]
    private string? _sourceFilter;

    /// <summary>
    /// ATT&amp;CK technique or tactic id prefix (<c>T1078</c>, <c>T1053</c>, <c>TA0002</c>).
    /// Matches by prefix so <c>T1053</c> catches <c>T1053.005</c>.
    /// </summary>
    [ObservableProperty]
    private string? _mitreFilter;

    [ObservableProperty]
    private string _statusLine = "No events yet.";

    [ObservableProperty]
    private TimelineEvent? _selectedEvent;

    [ObservableProperty]
    private string? _bookmarkNote;

    /// <summary>True while a folder is being read. Refresh and Export wait for it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(ExportCommand), nameof(ClearCommand))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowNoMatches))]
    private bool _isIngesting;

    /// <summary>True when a filter box or date has been changed since the grid was last refreshed.</summary>
    [ObservableProperty]
    private bool _filtersEdited;

    /// <summary>The filter the grid is showing. Export uses this, not the boxes.</summary>
    private AppliedFilter _applied;

    public TimelineToolViewModel()
    {
        _applied = Snapshot();
        Events.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(ShowNoMatches));
        };
    }

    /// <summary>True when at least one event has been ingested.</summary>
    public bool HasEvents => Timeline.Count > 0;

    /// <summary>Nothing ingested yet: the view offers "Ingest folder…".</summary>
    public bool ShowEmptyState => !HasEvents && !IsIngesting;

    /// <summary>Events exist but the applied filter matches none: the view says that, not "no events yet".</summary>
    public bool ShowNoMatches => HasEvents && Events.Count == 0 && !IsIngesting;

    partial void OnFromChanged(DateTimeOffset value) => FiltersEdited = true;
    partial void OnToChanged(DateTimeOffset value) => FiltersEdited = true;
    partial void OnUserFilterChanged(string? value) => FiltersEdited = true;
    partial void OnTextFilterChanged(string? value) => FiltersEdited = true;
    partial void OnSourceFilterChanged(string? value) => FiltersEdited = true;
    partial void OnMitreFilterChanged(string? value) => FiltersEdited = true;

    /// <summary>Flags the selected event as a finding in the open case; Reports turns it into an exhibit.</summary>
    [RelayCommand]
    private async Task BookmarkSelectedAsync(CancellationToken ct)
    {
        // Log to the case this work started under, even if the examiner switches case before it ends.
        var custodyCase = Cinder.App.Services.ActiveCaseContext.Current;
        var e = SelectedEvent;
        var session = ActiveCaseContext.Current;
        if (e is null)
        {
            StatusLine = "Select an event to bookmark.";
            return;
        }
        if (session?.Path is null)
        {
            StatusLine = "Open a case first — bookmarks live in the case file.";
            return;
        }
        try
        {
            var values = new Dictionary<string, string?>
            {
                ["Timestamp"] = e.Timestamp.ToString("O"),
                ["Source"] = e.Source,
                ["User"] = e.User,
                ["ATT&CK"] = e.TagsDisplay,
                ["Summary"] = e.Summary,
            };
            var title = $"Timeline: {e.Timestamp:u} {e.Source} — {(e.Summary.Length > 60 ? e.Summary[..60] + "…" : e.Summary)}";
            var store = new Cinder.Core.Cases.BookmarkStore(new Cinder.Core.Cases.CaseStore(session.Path));
            var bm = await store.AddAsync(session.Id, Environment.UserName, "timeline", null, title, BookmarkNote,
                System.Text.Json.JsonSerializer.Serialize(values), ct);
            await ActiveCaseContext.LogAsync(custodyCase, CustodyAction.Annotation, new
            {
                Kind = "bookmark",
                BookmarkId = bm.Id,
                Tool = "timeline",
                Title = title,
                Note = BookmarkNote,
            }, ct);
            StatusLine = $"Bookmarked #{bm.Id}: {title}";
            BookmarkNote = null;
        }
        catch (Exception ex)
        {
            StatusLine = $"Could not add the bookmark: {ex.Message}";
        }
    }

    /// <summary>Ids the tagger can emit, for the filter's suggestion list.</summary>
    public IReadOnlyList<string> KnownMitreIds { get; } =
        MitreTagger.KnownIds.OrderBy(id => id, StringComparer.Ordinal).Select(MitreTagger.Describe).ToArray();

    /// <summary>
    /// A filter as applied: the date window, the library filter (user, text, technique) and the
    /// source terms, which are matched here because a source is named like
    /// <c>evtx.Security</c> and an examiner types <c>evtx</c>.
    /// </summary>
    internal readonly record struct AppliedFilter(
        DateTimeOffset From, DateTimeOffset To, TimelineFilter Filter, IReadOnlyList<string> SourceTerms)
    {
        public bool Matches(TimelineEvent e) => SourceMatches(e.Source, SourceTerms);
    }

    private AppliedFilter Snapshot() => new(
        From,
        InclusiveEnd(To),
        new TimelineFilter(
            User: string.IsNullOrWhiteSpace(UserFilter) ? null : UserFilter.Trim(),
            TextContains: string.IsNullOrWhiteSpace(TextFilter) ? null : TextFilter.Trim(),
            MitreTechnique: NormaliseMitre(MitreFilter)),
        ParseSourceTerms(SourceFilter));

    /// <summary>
    /// A "to" date picked in the calendar is midnight at the start of that day. The examiner
    /// means the whole day, so the window runs to its last instant.
    /// </summary>
    internal static DateTimeOffset InclusiveEnd(DateTimeOffset to) =>
        to.TimeOfDay == TimeSpan.Zero && to < DateTimeOffset.MaxValue.AddDays(-1) ? to.AddDays(1).AddTicks(-1) : to;

    /// <summary>Splits "evtx, browser.history" into its terms. Empty input means "any source".</summary>
    internal static IReadOnlyList<string> ParseSourceTerms(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when there are no terms, or the source contains any of them, ignoring case.</summary>
    internal static bool SourceMatches(string source, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return true;
        }
        foreach (var term in terms)
        {
            if (source.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

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

    /// <summary>Every event matching <paramref name="filter"/>, in time order.</summary>
    private IEnumerable<TimelineEvent> Matching(AppliedFilter filter)
    {
        foreach (var e in Timeline.Range(filter.From, filter.To, filter.Filter))
        {
            if (filter.Matches(e))
            {
                yield return e;
            }
        }
    }

    /// <summary>What Export writes: everything matching the filter the grid is showing.</summary>
    internal List<TimelineEvent> EventsForExport() => [.. Matching(_applied)];

    private bool CanUseTimeline() => !IsIngesting;

    /// <summary>Applies the filter boxes: the grid, the histogram and later exports all use this snapshot.</summary>
    [RelayCommand(CanExecute = nameof(CanUseTimeline))]
    private void Refresh()
    {
        _applied = Snapshot();
        FiltersEdited = false;
        Events.Clear();
        Histogram.Clear();

        var applied = _applied;
        var counts = new int[HistogramBuckets];
        var span = (applied.To - applied.From).Ticks;
        var matched = 0;
        foreach (var e in Matching(applied))
        {
            if (matched < MaxShown)
            {
                Events.Add(e);
            }
            matched++;
            if (span > 0)
            {
                var bucket = (int)((e.Timestamp - applied.From).Ticks * (double)HistogramBuckets / span);
                counts[Math.Clamp(bucket, 0, HistogramBuckets - 1)]++;
            }
        }
        // Bar heights are scaled to the strip; raw counts would overflow it.
        var peak = counts.Max();
        foreach (var c in counts)
        {
            Histogram.Add(peak == 0 ? 0 : c == 0 ? 0 : Math.Max(2, c * HistogramHeight / peak));
        }

        StatusLine = DescribeShown(Events.Count, matched, Timeline.Count);
        OnPropertyChanged(nameof(HasEvents));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowNoMatches));
    }

    /// <summary>The status line under the histogram. Says plainly when the grid is a prefix of what matched.</summary>
    internal static string DescribeShown(int shown, int matched, int total)
    {
        if (total == 0)
        {
            return "No events yet.";
        }
        if (matched == 0)
        {
            return $"No events match the dates and filters. The timeline holds {total:N0} events.";
        }
        return matched > shown
            ? $"Showing the first {shown:N0} of {matched:N0} matching events, earliest first. Narrow the dates or filters to see the rest. Export writes all {matched:N0}."
            : matched == total
                ? $"Showing all {total:N0} events."
                : $"Showing {matched:N0} of {total:N0} events.";
    }

    /// <summary>
    /// Writes every event matching the applied filter — not only the rows the grid shows — as
    /// Timesketch JSONL, Timesketch CSV, or the Sleuth Kit body format.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseTimeline))]
    private async Task ExportAsync(string format, CancellationToken ct)
    {
        // Log to the case this work started under, even if the examiner switches case before it ends.
        var custodyCase = Cinder.App.Services.ActiveCaseContext.Current;
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
            var applied = _applied;
            var events = EventsForExport();

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

            StatusLine = $"Exported {written:N0} events to {path}" +
                         (FiltersEdited ? ". The export used the filters the grid is showing; the changes in the filter boxes have not been applied." : "");
            await ActiveCaseContext.LogAsync(custodyCase, CustodyAction.DataExported, new
            {
                Tool = "timeline",
                Format = label,
                Path = path,
                Events = written,
                applied.From,
                applied.To,
                Filter = new
                {
                    applied.Filter.User,
                    applied.Filter.TextContains,
                    Source = string.Join(", ", applied.SourceTerms),
                    Mitre = applied.Filter.MitreTechnique,
                },
            }, ct);
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Export stopped.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Could not export the timeline: {ex.Message}";
        }
    }

    /// <summary>
    /// Reads a folder of evidence into the timeline. The new timeline is assembled away from the
    /// one on screen and swapped in when complete; stopping part-way leaves the timeline exactly
    /// as it was.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task IngestFolderAsync(CancellationToken ct)
    {
        // Log to the case this work started under, even if the examiner switches case before it ends.
        var custodyCase = Cinder.App.Services.ActiveCaseContext.Current;
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick a folder of evidence to add to the timeline",
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        IsIngesting = true;
        StatusLine = $"Reading {path}…";
        var progress = new Progress<string>(s =>
        {
            if (IsIngesting)
            {
                StatusLine = $"Reading · {s}";
            }
        });
        try
        {
            var result = await IngestIntoCopyAsync(Timeline, path, progress, ct);
            Timeline = result.Timeline;
            OnPropertyChanged(nameof(Timeline));
            if (result.Earliest is { } first && result.Latest is { } last)
            {
                // Widen the window so everything just read is visible.
                From = AddDaysClamped(first, -1);
                To = AddDaysClamped(last, 1);
            }
            IsIngesting = false;
            Refresh();
            var added = result.Timeline.Count - result.CountBefore;
            StatusLine = added == 0
                ? $"No timestamped events were found under {path}. {result.Stats}"
                : $"Added {added:N0} events from {path} ({result.Stats}). {StatusLine}";
            await ActiveCaseContext.LogAsync(custodyCase, CustodyAction.ParserRan, new
            {
                Tool = "timeline.ingest",
                Folder = path,
                Added = added,
                Events = Timeline.Count,
                Stats = result.Stats.ToString(),
            }, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Stopped. Nothing from this folder was added.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Could not read {path}: {ex.Message}";
        }
        finally
        {
            IsIngesting = false;
        }
    }

    internal sealed record IngestResult(
        SuperTimeline Timeline, IngestStats Stats, int CountBefore, DateTimeOffset? Earliest, DateTimeOffset? Latest);

    /// <summary>
    /// Builds a new timeline holding <paramref name="existing"/>'s events plus everything under
    /// <paramref name="folder"/>, without modifying <paramref name="existing"/>. Runs entirely
    /// on the thread pool.
    /// </summary>
    internal static Task<IngestResult> IngestIntoCopyAsync(
        SuperTimeline existing, string folder, IProgress<string>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        var next = new SuperTimeline();
        foreach (var e in existing.Range(DateTimeOffset.MinValue, DateTimeOffset.MaxValue))
        {
            ct.ThrowIfCancellationRequested();
            // Tags are passed through so events are not re-tagged, or tagged differently.
            next.Add(new CarriedEvent(e.Source, e.User, e.Timestamp, e.Summary), e.Tags);
        }
        var before = next.Count;
        var stats = await TimelineIngester.IngestAsync(next, folder, progress, ct);
        ct.ThrowIfCancellationRequested();
        next.Sort();

        DateTimeOffset? earliest = null, latest = null;
        foreach (var e in next.Range(DateTimeOffset.MinValue, DateTimeOffset.MaxValue))
        {
            earliest ??= e.Timestamp;
            latest = e.Timestamp;
        }
        return new IngestResult(next, stats, before, earliest, latest);
    }, ct);

    private static DateTimeOffset AddDaysClamped(DateTimeOffset t, int days)
    {
        try
        {
            return t.AddDays(days);
        }
        catch (ArgumentOutOfRangeException)
        {
            return t;
        }
    }

    /// <summary>Empties the timeline. Evidence is untouched; ingest the folders again to rebuild it.</summary>
    [RelayCommand(CanExecute = nameof(CanUseTimeline))]
    private void Clear()
    {
        Timeline = new SuperTimeline();
        OnPropertyChanged(nameof(Timeline));
        Refresh();
        StatusLine = "Timeline cleared.";
    }

    private sealed record CarriedEvent(string Source, string? User, DateTimeOffset? Timestamp, string Summary)
        : Cinder.Artifacts.ArtifactBase(Source, User, Timestamp, Summary);
}
