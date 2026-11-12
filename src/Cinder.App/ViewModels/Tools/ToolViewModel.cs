using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cinder.App.ViewModels.Tools;

/// <summary>One section in the left activity rail (e.g. "Examine", "Analyze").</summary>
public sealed partial class ToolSection : ObservableObject
{
    public string Title { get; init; } = "";
    public ObservableCollection<ToolViewModel> Tools { get; init; } = new();

    /// <summary>Tools after the rail filter is applied. The rail binds to this, not <see cref="Tools"/>.</summary>
    public ObservableCollection<ToolViewModel> VisibleTools { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>True when the filter hides every tool in the section, so the whole group can collapse away.</summary>
    [ObservableProperty]
    private bool _isEmpty;

    public int VisibleCount => VisibleTools.Count;

    /// <summary>Re-applies <paramref name="filter"/>. Settings is pinned at the rail foot, so it is skipped here.</summary>
    public void ApplyFilter(string? filter)
    {
        VisibleTools.Clear();
        foreach (var t in Tools)
        {
            if (t.Kind == "settings")
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(filter) || t.Matches(filter))
            {
                VisibleTools.Add(t);
            }
        }
        IsEmpty = VisibleTools.Count == 0;
        OnPropertyChanged(nameof(VisibleCount));
    }
}

/// <summary>
/// Base class for every "tool" surfaced in the left rail. Each derived class typically wraps a
/// service or sidecar (registry parser, super-timeline, report builder, etc.). Tools that aren't
/// yet wired to real evidence still expose the right loader UI so the user knows what they do.
/// </summary>
public abstract partial class ToolViewModel : ViewModelBase
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Icon { get; }
    public virtual string Phase => "";
    public virtual string Subtitle => "";

    /// <summary>"hex" | "filesystem" | "registry" | … — drives the content selector in the shell.</summary>
    public abstract string Kind { get; }

    /// <summary>Stroke icon for the rail, header and empty state (replaces the emoji <see cref="Icon"/>).</summary>
    public Avalonia.Media.Geometry IconGeometry => Controls.Icons.ForTool(Id);

    /// <summary>
    /// Honest maturity label shown as a badge in the rail: empty when the tool works end to end,
    /// otherwise one word that tells the examiner what to expect before they click. Kept in one
    /// table so it can be audited against docs/FEATURES.md at a glance.
    /// </summary>
    public string Maturity => MaturityById.TryGetValue(Id, out var m) ? m : "";

    public bool HasMaturity => Maturity.Length > 0;

    public string MaturityTip => Maturity switch
    {
        "partial" => "Works, with gaps. The ? help lists what is not covered yet.",
        "subset" => "Supports part of the format or rule language, not all of it.",
        "external" => "Needs a separate program installed (for example Volatility 3) before it can run.",
        "shell" => "Preview only: this screen does not do anything yet.",
        _ => "",
    };

    private static readonly Dictionary<string, string> MaturityById = new(StringComparer.Ordinal)
    {
        ["email"] = "partial",
        ["memory"] = "external",
        ["yara"] = "subset",
        ["mount"] = "partial",
        ["ramcapture"] = "shell",
        ["cloud"] = "shell",
        ["plugins"] = "partial",
    };

    /// <summary>Rail filter match: title, subtitle or kind, case-insensitive.</summary>
    public bool Matches(string filter) =>
        Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        Kind.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>One sentence for the empty state: what to give this tool. Each tool writes its own.</summary>
    public virtual string? EmptyStateHint => null;

    /// <summary>Python packages a sidecar-driven tool needs in the bundled venv.</summary>
    public virtual IReadOnlyList<string> RequiredPythonPackages => Array.Empty<string>();

    /// <summary>
    /// Long-form help for the "?" affordance in the tool header. Plain text with newline
    /// paragraphs and "## Section" headings; rendered by the help flyout into bold/regular runs.
    /// Tools that don't override this get a placeholder.
    /// </summary>
    public virtual string HelpMarkdown =>
        $"## What this is\n{Title} — {Subtitle}\n\n" +
        "## Status\nDetailed help for this tool is not yet written. " +
        "Check ROADMAP.md for what's planned, or visit the project's GitHub for usage notes.";

    /// <summary>Whether the "?" button should appear in the tool header.</summary>
    public bool HasHelp => !string.IsNullOrWhiteSpace(HelpMarkdown);

    private IReadOnlyList<HelpBlock>? _helpBlocks;

    /// <summary>
    /// <see cref="HelpMarkdown"/> parsed into a list of typed blocks the view can render with a
    /// data-template per Kind. Parsing happens once and is cached. Moving the parser out of
    /// code-behind into the view-model means the help body is purely binding-driven (no
    /// fragile FindControl / Application.Current.Resources lookups).
    /// </summary>
    public IReadOnlyList<HelpBlock> HelpBlocks => _helpBlocks ??= ParseHelp(HelpMarkdown);

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Raised when a tool becomes the one on screen. View-models that are not tools themselves
    /// (the report builder) use it to bring case-bound lists up to date when their tool opens.
    /// </summary>
    public static event Action<ToolViewModel>? ToolSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            OnSelected();
            ToolSelected?.Invoke(this);
        }
    }

    /// <summary>Called each time this tool is brought on screen.</summary>
    protected virtual void OnSelected()
    {
    }

    private static IReadOnlyList<HelpBlock> ParseHelp(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return Array.Empty<HelpBlock>();
        }

        var blocks = new List<HelpBlock>();
        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count == 0)
            {
                return;
            }
            blocks.Add(new HelpBlock(HelpBlockKind.Paragraph,
                string.Join(" ", paragraph.Select(l => l.TrimEnd()))));
            paragraph.Clear();
        }

        foreach (var raw in source.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new HelpBlock(HelpBlockKind.Heading, line[3..].Trim().ToUpperInvariant()));
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new HelpBlock(HelpBlockKind.Bullet, line[2..].Trim(), Marker: "•"));
                continue;
            }
            var dot = line.IndexOf(". ", StringComparison.Ordinal);
            if (dot is > 0 and < 4 && int.TryParse(line[..dot], out _))
            {
                Flush();
                blocks.Add(new HelpBlock(HelpBlockKind.Bullet, line[(dot + 2)..].Trim(), Marker: line[..dot] + "."));
                continue;
            }
            paragraph.Add(line);
        }
        Flush();

        return blocks;
    }
}

/// <summary>One renderable block in a tool's help body.</summary>
public sealed record HelpBlock(HelpBlockKind Kind, string Text, string Marker = "")
{
    public bool IsHeading => Kind == HelpBlockKind.Heading;
    public bool IsParagraph => Kind == HelpBlockKind.Paragraph;
    public bool IsBullet => Kind == HelpBlockKind.Bullet;
}

public enum HelpBlockKind { Heading, Paragraph, Bullet }

/// <summary>
/// What a parser hands back to <see cref="SidecarToolViewModel"/>: the rows, whether it stopped
/// against a row budget, and an optional one-line note for the status bar ("3 files could not
/// be read"). Parsers return this instead of writing to the grid themselves, so a run that was
/// superseded or stopped can never leak its rows into the next file's grid.
/// </summary>
public sealed record ParseResult(
    IReadOnlyList<object> Rows,
    bool Truncated = false,
    long? AvailableRows = null,
    string? Note = null,
    string? TruncationReason = null,
    object? CustodyDetail = null)
{
    // CustodyDetail: anything about the run that weakens or qualifies its result and so
    // belongs in the custody log beside the row count: files that could not be read, damaged
    // chunks read as zeros.
    // TruncationReason: the sentence for the cut-short banner when the usual one would be
    // wrong. A capture that ends in a damaged record did not hit a row limit, and saying
    // "Cinder loaded only part of it" would blame the wrong thing.

    /// <summary>
    /// Flags truncation when the parser produced its whole budget. Landing exactly on the cap
    /// means it stopped there rather than running out of input. (An artifact with exactly
    /// <paramref name="budget"/> rows is flagged too; over-warning is the safe direction.)
    /// </summary>
    public static ParseResult WithBudget(IReadOnlyList<object> rows, int budget, string? note = null) =>
        new(rows, rows.Count >= budget, null, note);
}

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can swap its whole content with one Reset
/// notification. A 200,000-row journal added one row at a time raises 200,000 change events on
/// the UI thread; this raises one.
/// </summary>
internal sealed class RowCollection : ObservableCollection<object>
{
    public void ReplaceAll(IEnumerable<object> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>
/// The shared "pick evidence, parse it, show rows" tool. Most parser tabs (registry, event log,
/// prefetch, …) inherit this. Subclasses implement <see cref="ParseAsync"/>; this class owns the
/// grid, the row filter, export, bookmarking, the truncation and error banners, and the rule
/// that only the most recent run may touch any of them.
/// </summary>
public abstract partial class SidecarToolViewModel : ToolViewModel
{
    /// <summary>Below this many rows the filter applies on every keystroke; above it, after a short pause.</summary>
    internal const int ImmediateFilterRows = 2_000;

    /// <summary>Separates column values in a row's search text so a filter cannot match across two columns.</summary>
    internal const char ColumnSeparator = (char)0x1F;

    private static readonly TimeSpan FilterPause = TimeSpan.FromMilliseconds(180);

    private readonly RowCollection _rows = new();
    private readonly RowCollection _visibleRows = new();

    /// <summary>Search text per row, aligned with <see cref="Rows"/>. Built once per load, on first filter use.</summary>
    private string[]? _searchText;

    /// <summary>Identifies the newest load. A run whose number is no longer this one has been superseded.</summary>
    private int _runSerial;

    /// <summary>Identifies the newest filter request, so a slower earlier one cannot overwrite it.</summary>
    private int _filterSerial;

    private bool _filterPending;
    private bool _errorIsFromExport;

    /// <summary>Every row the parser produced (subject to its budget).</summary>
    public ObservableCollection<object> Rows => _rows;

    /// <summary>What the grid shows: <see cref="Rows"/> after <see cref="RowFilter"/>.</summary>
    public ObservableCollection<object> VisibleRows => _visibleRows;

    /// <summary>
    /// Case-insensitive text to find in any column of a row. Typing <c>Unknown</c> in the
    /// Filesystem tool after a hashed walk is the known-good filter.
    /// </summary>
    [ObservableProperty]
    private string? _rowFilter;

    /// <summary>The row the examiner has selected in the grid — what "Bookmark" acts on.</summary>
    [ObservableProperty]
    private object? _selectedRow;

    /// <summary>Free-text note attached to the next bookmark.</summary>
    [ObservableProperty]
    private string? _bookmarkNote;

    /// <summary>True when this tool can read a folder (a Prefetch directory, a browser profile, an iOS backup).</summary>
    public virtual bool AcceptsFolder => false;

    /// <summary>True when this tool can read a single file. False only for folder-only tools.</summary>
    public virtual bool AcceptsFile => true;

    /// <summary>The line under the empty-state buttons: what can be dropped on the tool.</summary>
    public string DropHint => (AcceptsFile, AcceptsFolder) switch
    {
        (true, true) => "or drop a file or folder here",
        (false, true) => "or drop a folder here",
        _ => "or drop a file here",
    };

    partial void OnRowFilterChanged(string? value) => ScheduleFilter();

    /// <summary>
    /// Applies the filter straight away on small grids, and after a short pause on large ones
    /// with the matching done off the UI thread, so typing never stalls on a 200,000-row journal.
    /// </summary>
    private void ScheduleFilter()
    {
        var serial = ++_filterSerial;
        if (_rows.Count <= ImmediateFilterRows)
        {
            Reproject();
            return;
        }
        _filterPending = true;
        _ = FilterAfterPauseAsync(serial);
    }

    private async Task FilterAfterPauseAsync(int serial)
    {
        try
        {
            await Task.Delay(FilterPause);
            if (serial != _filterSerial)
            {
                return;
            }
            var rows = _rows.ToList();
            var needle = Needle;
            var known = _searchText;
            var (text, matches) = await Task.Run(() =>
            {
                var t = known is not null && known.Length == rows.Count ? known : BuildSearchText(rows);
                return (t, FilterRows(rows, t, needle));
            });
            if (serial != _filterSerial)
            {
                return;
            }
            _searchText = text;
            _filterPending = false;
            _visibleRows.ReplaceAll(matches);
            OnPropertyChanged(nameof(RowCountDisplay));
            OnPropertyChanged(nameof(ShowNoRows));
        }
        catch (Exception ex)
        {
            // A filter must never take the tool down; the grid keeps what it was showing.
            Serilog.Log.Warning(ex, "Row filter failed in {Tool}", Id);
        }
    }

    private string Needle => (RowFilter ?? "").Trim();

    /// <summary>Rebuilds <see cref="VisibleRows"/> from <see cref="Rows"/> under the current filter, now.</summary>
    protected void Reproject()
    {
        _filterSerial++;
        _filterPending = false;
        var needle = Needle;
        if (needle.Length == 0)
        {
            _visibleRows.ReplaceAll(_rows.ToList());
        }
        else
        {
            if (_searchText is null || _searchText.Length != _rows.Count)
            {
                _searchText = BuildSearchText(_rows);
            }
            _visibleRows.ReplaceAll(FilterRows(_rows, _searchText, needle));
        }
        OnPropertyChanged(nameof(RowCountDisplay));
        OnPropertyChanged(nameof(ShowNoRows));
    }

    /// <summary>
    /// One search string per row: every column's value as the export would write it, joined by
    /// a separator no examiner types. Built once so filtering is a plain substring test per row
    /// instead of a reflection pass over every column on every keystroke.
    /// </summary>
    internal static string[] BuildSearchText(IReadOnlyList<object> rows)
    {
        var text = new string[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            text[i] = string.Join(ColumnSeparator,
                Cinder.Core.Export.TabularExporter.ToDictionary(rows[i]).Values.Where(v => !string.IsNullOrEmpty(v)));
        }
        return text;
    }

    /// <summary>The rows whose search text contains <paramref name="needle"/>, ignoring case. Order is kept.</summary>
    internal static List<object> FilterRows(IReadOnlyList<object> rows, IReadOnlyList<string> searchText, string needle)
    {
        if (needle.Length == 0)
        {
            return [.. rows];
        }
        var matches = new List<object>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (searchText[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(rows[i]);
            }
        }
        return matches;
    }

    /// <summary>
    /// Flags the selected row as a finding in the active case. Stored with the tool, the
    /// evidence path, the note and the row's values, and echoed into the custody log; the
    /// Reports tool turns bookmarks into exhibits.
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task BookmarkSelectedAsync(CancellationToken ct)
    {
        // Log to the case this work started under, even if the examiner switches case before it ends.
        var custodyCase = Cinder.App.Services.ActiveCaseContext.Current;
        var row = SelectedRow;
        var session = Services.ActiveCaseContext.Current;
        if (row is null)
        {
            StatusLine = "Select a row to bookmark.";
            return;
        }
        if (session?.Path is null)
        {
            StatusLine = "Open a case first — bookmarks live in the case file.";
            return;
        }

        try
        {
            var values = Cinder.Core.Export.TabularExporter.ToDictionary(row);
            var headline = values.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "(row)";
            var title = $"{Title}: {Truncate(headline, 80)}";
            var json = System.Text.Json.JsonSerializer.Serialize(values);

            var store = new Cinder.Core.Cases.BookmarkStore(new Cinder.Core.Cases.CaseStore(session.Path));
            var bm = await store.AddAsync(session.Id, Environment.UserName, Id, CustodyEvidence, title, BookmarkNote, json, ct);

            await Services.ActiveCaseContext.LogAsync(custodyCase, Cinder.Core.Custody.CustodyAction.Annotation, new
            {
                Kind = "bookmark",
                BookmarkId = bm.Id,
                Tool = Id,
                Evidence = CustodyEvidence,
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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoRows))]
    private string? _evidencePath;

    /// <summary>File or folder name of the loaded evidence, for the toolbar chip (full path in its tooltip).</summary>
    public string? EvidenceName => DisplayName(EvidencePath);

    private static string? DisplayName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    partial void OnEvidencePathChanged(string? value) => OnPropertyChanged(nameof(EvidenceName));

    /// <summary>"1,204 rows" or "38 of 1,204 rows" when the filter is narrowing.</summary>
    public string RowCountDisplay => VisibleRows.Count == Rows.Count
        ? $"{Rows.Count:N0} row{(Rows.Count == 1 ? "" : "s")}"
        : $"{VisibleRows.Count:N0} of {Rows.Count:N0} rows";

    [ObservableProperty]
    private string? _statusLine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoRows))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoRows))]
    private string? _errorMessage;

    /// <summary>Heading for the error banner: what failed, in the examiner's terms.</summary>
    [ObservableProperty]
    private string _errorTitle = "Could not read this evidence";

    /// <summary>
    /// Set by a parser that stopped early against a row budget. Several parsers cap how much
    /// they materialize so a 500 MB SOFTWARE hive can't lock the UI; when that cap bites, the
    /// grid is showing a prefix of the evidence rather than all of it. Saying so is not
    /// optional — an examiner who reads a truncated view as complete draws a wrong conclusion
    /// from it, and nothing on screen would otherwise contradict them.
    /// </summary>
    [ObservableProperty]
    private bool _isTruncated;

    /// <summary>
    /// The sentence shown in the truncation banner. Kept apart from <see cref="StatusLine"/> so
    /// a later "Bookmarked #3" or "Exported…" cannot replace the warning.
    /// </summary>
    [ObservableProperty]
    private string? _truncationNotice;

    /// <summary>Total rows the parser would have produced, when it knows. Null if unknown.</summary>
    [ObservableProperty]
    private long? _availableRowCount;

    /// <summary>The parse finished, found nothing, and did not fail: the grid area says so instead of sitting blank.</summary>
    public bool ShowNoRows => !IsLoading && ErrorMessage is null && EvidencePath is not null && Rows.Count == 0;

    /// <summary>
    /// Subclasses parse the evidence here and return the rows. They must not touch
    /// <see cref="Rows"/> themselves. A wrong input (the SOFTWARE hive given to USB history) or
    /// a parser failure is thrown, so it reaches the error banner instead of looking like an
    /// artifact with nothing in it. Cancellation must be allowed to propagate.
    /// </summary>
    protected abstract Task<ParseResult> ParseAsync(string evidencePath, CancellationToken ct);

    /// <summary>
    /// The evidence this tool examined, as written to the custody log and stored on bookmarks.
    /// For nearly every tool that is the picked path. The indicator tool is the exception: it
    /// is given a list and scans a folder, and the folder is the evidence.
    /// </summary>
    protected virtual string? CustodyEvidence => EvidencePath;

    /// <summary>
    /// Something the run was given that is not evidence, recorded beside it so the entry says
    /// what the evidence was examined with. The indicator tool's list is one.
    /// </summary>
    protected virtual string? CustodyInput => null;

    /// <summary>The custody entry for one run: what was examined, with what, and how much of it was shown.</summary>
    internal object DescribeRunForCustody(int rows, bool truncated, object? detail = null) => new
    {
        Tool = Id,
        Evidence = CustodyEvidence,
        Input = CustodyInput,
        Rows = rows,
        Truncated = truncated,
        Detail = detail,
    };

    /// <summary>The heading of the error banner when a run fails.</summary>
    protected virtual string FailureTitle(string path) =>
        Directory.Exists(path) ? "Could not read this folder" : "Could not read this file";

    /// <summary>The status line after the examiner stops a run.</summary>
    protected virtual string StoppedStatus => "Stopped. Nothing was loaded.";

    /// <summary>The status line when a run fails; the banner carries the reason.</summary>
    protected virtual string FailureStatus(string name) => $"Could not read {name}.";

    /// <summary>
    /// Empties the grid and the banners, and makes any run still in flight a superseded one.
    /// For tools whose results stop being true when an input changes.
    /// </summary>
    protected void ClearResults(string? status = null)
    {
        LoadEvidenceCommand.Cancel();
        _runSerial++;
        _filterSerial++;
        _filterPending = false;
        _errorIsFromExport = false;
        _searchText = null;
        ErrorMessage = null;
        IsLoading = false;
        IsTruncated = false;
        TruncationNotice = null;
        AvailableRowCount = null;
        SelectedRow = null;
        _rows.ReplaceAll([]);
        _visibleRows.ReplaceAll([]);
        OnPropertyChanged(nameof(RowCountDisplay));
        OnPropertyChanged(nameof(ShowNoRows));
        StatusLine = status;
    }

    /// <summary>
    /// Checks the picked path before parsing. Returns the message to show, or null when the
    /// path is something this tool can read.
    /// </summary>
    protected virtual string? CheckInput(string path)
    {
        if (Directory.Exists(path))
        {
            return AcceptsFolder ? null : $"{Title} reads a single file. Pick the file itself rather than the folder it is in.";
        }
        if (!File.Exists(path))
        {
            return $"{path} does not exist or cannot be opened.";
        }
        return AcceptsFile ? null : $"{Title} reads a folder. Pick the folder rather than a file inside it.";
    }

    /// <summary>
    /// Writes the current grid to CSV or JSON. Exports exactly the columns and rows the grid
    /// shows; string cells are guarded against spreadsheet formula injection, since a filename
    /// in evidence beginning with <c>=</c> is a realistic thing to find.
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task ExportAsync(string format, CancellationToken ct)
    {
        // Log to the case this work started under, even if the examiner switches case before it ends.
        var custodyCase = Cinder.App.Services.ActiveCaseContext.Current;
        if (_filterPending)
        {
            // The examiner typed a filter and went straight to Export: write what they asked for.
            Reproject();
        }
        if (VisibleRows.Count == 0)
        {
            StatusLine = Rows.Count == 0 ? "Nothing to export yet." : "Nothing to export: the filter matches no rows.";
            return;
        }
        var ext = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "csv";
        var path = await ToolDialog.SaveFileAsync($"Export {Title} as {ext.ToUpperInvariant()}", $"{Id}-export.{ext}", ext);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        await ExportToAsync(path, ext, custodyCase, ct);
    }

    /// <summary>
    /// Writes the rows the grid shows to <paramref name="path"/>. A failure here is a failure
    /// to save a file, not to read evidence, and the banner heading says so: the rows on screen
    /// are still good.
    /// </summary>
    internal async Task ExportToAsync(string path, string ext, CaseSession? custodyCase, CancellationToken ct)
    {
        if (_errorIsFromExport)
        {
            ErrorMessage = null;
            _errorIsFromExport = false;
        }

        // What the grid shows — the row filter is part of what the examiner chose to export.
        var snapshot = VisibleRows.ToList();
        try
        {
            var written = await Task.Run(() =>
            {
                if (ext == "json")
                {
                    using var fs = File.Create(path);
                    return Cinder.Core.Export.TabularExporter.WriteJson(snapshot, fs);
                }
                using var w = new StreamWriter(path, append: false, System.Text.Encoding.UTF8);
                return Cinder.Core.Export.TabularExporter.WriteCsv(snapshot, w);
            }, ct);

            StatusLine = $"Exported {written:N0} row{(written == 1 ? "" : "s")} to {path}" +
                         (IsTruncated ? ". The grid was cut short, so the export holds only the rows shown, not the whole artifact." : "");
            await Services.ActiveCaseContext.LogAsync(custodyCase, Cinder.Core.Custody.CustodyAction.DataExported, new
            {
                Tool = Id,
                Format = ext,
                Path = path,
                Rows = written,
                Evidence = CustodyEvidence,
                Input = CustodyInput,
                Truncated = IsTruncated,
            }, ct);
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Export stopped.";
        }
        catch (Exception ex)
        {
            ErrorTitle = "Could not save the export";
            ErrorMessage = ex.Message;
            _errorIsFromExport = true;
            StatusLine = $"Could not export to {path}.";
        }
    }

    /// <summary>
    /// Parses <paramref name="path"/> and shows the result. Starting a load while another is
    /// running supersedes it: the earlier run is cancelled and, whatever it does afterwards, it
    /// may not touch the grid, the banners or the busy flag. "Stop" cancels the current run.
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand(IncludeCancelCommand = true)]
    private async Task LoadEvidenceAsync(string? path, CancellationToken ct)
    {
        // Log to the case this work started under, even if the examiner switches case before it ends.
        var custodyCase = Cinder.App.Services.ActiveCaseContext.Current;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        var run = ++_runSerial;
        _filterSerial++;
        _filterPending = false;
        _errorIsFromExport = false;

        EvidencePath = path;
        ErrorMessage = null;
        IsLoading = true;
        IsTruncated = false;
        TruncationNotice = null;
        AvailableRowCount = null;
        SelectedRow = null;
        _searchText = null;
        _rows.ReplaceAll([]);
        _visibleRows.ReplaceAll([]);
        OnPropertyChanged(nameof(RowCountDisplay));
        OnPropertyChanged(nameof(ShowNoRows));
        var name = DisplayName(path) ?? path;
        StatusLine = $"Reading {name}…";

        try
        {
            if (CheckInput(path) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            var result = await ParseAsync(path, ct);
            if (run != _runSerial)
            {
                return;
            }
            ct.ThrowIfCancellationRequested();

            var rows = result.Rows as List<object> ?? [.. result.Rows];
            var needle = Needle;
            string[]? text = null;
            var visible = rows;
            if (needle.Length > 0)
            {
                (text, visible) = await Task.Run(() =>
                {
                    var t = BuildSearchText(rows);
                    return (t, FilterRows(rows, t, needle));
                }, ct);
                if (run != _runSerial)
                {
                    return;
                }
            }

            _searchText = text;
            _rows.ReplaceAll(rows);
            _visibleRows.ReplaceAll(visible);
            IsTruncated = result.Truncated;
            AvailableRowCount = result.AvailableRows;
            OnPropertyChanged(nameof(RowCountDisplay));
            OnPropertyChanged(nameof(ShowNoRows));

            if (result.Truncated)
            {
                TruncationNotice = result.TruncationReason ?? DescribeTruncation(rows.Count, result.AvailableRows);
                StatusLine = result.Note ?? TruncationNotice;
            }
            else
            {
                StatusLine = result.Note ?? (rows.Count == 0 ? $"Nothing to show from {name}." : $"Read {name}.");
            }

            // Every parser run against evidence is an evidential act — it goes in the custody
            // log with what was parsed and how much of it the examiner actually saw.
            await Services.ActiveCaseContext.LogAsync(custodyCase, Cinder.Core.Custody.CustodyAction.ParserRan,
                DescribeRunForCustody(rows.Count, result.Truncated, result.CustodyDetail), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            if (run == _runSerial)
            {
                StatusLine = StoppedStatus;
            }
        }
        catch (Exception ex)
        {
            if (run == _runSerial)
            {
                ErrorTitle = FailureTitle(path);
                ErrorMessage = ex.Message;
                StatusLine = FailureStatus(name);
            }
        }
        finally
        {
            if (run == _runSerial)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// The truncation sentence. It must not suggest that exporting recovers the missing rows:
    /// export writes the grid, and the grid is what was cut short.
    /// </summary>
    internal static string DescribeTruncation(int shown, long? available) =>
        $"Showing the first {shown:N0} rows" +
        (available is { } total && total > shown ? $" of {total:N0}" : "") +
        ". This evidence holds more than Cinder loaded, so this is not the whole artifact. " +
        "Export and bookmarks cover only the rows shown.";
}
