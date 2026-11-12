using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels.Tools;

/// <summary>
/// Owns the four sections (Examine / Analyze / Acquire / Case) of the activity rail and the
/// currently-selected tool. The MainWindowViewModel composes one of these.
/// </summary>
public sealed partial class WorkspaceViewModel : ViewModelBase
{
    public ObservableCollection<ToolSection> Sections { get; }

    [ObservableProperty]
    private ToolViewModel? _selectedTool;

    /// <summary>
    /// The Dashboard / Home tool. Kept as a typed reference so the rest of the app can update
    /// "recent cases" and "recent evidence" on it without re-resolving through FindByKind.
    /// </summary>
    public DashboardTool Dashboard { get; }

    public WorkspaceViewModel()
    {
        Dashboard = new DashboardTool();

        Sections =
        [
            new()
            {
                Title = "Home",
                Tools = [Dashboard],
            },
            new()
            {
                Title = "Examine",
                Tools =
                [
                    new HexTool(),
                    new StringsTool(),
                    new GalleryTool(),
                    new DocumentsTool(),
                    new FilesystemTool(),
                    new UsnJournalTool(),
                    new LogFileTool(),
                    new RegistryTool(),
                    new EventLogTool(),
                    new PrefetchTool(),
                    new ShellbagsTool(),
                    new JumplistsTool(),
                    new LnkTool(),
                    new BrowserHistoryTool(),
                    new UsbHistoryTool(),
                    new WifiHistoryTool(),
                    new SrumTool(),
                    new AmcacheTool(),
                    new ShimcacheTool(),
                    new RecycleBinTool(),
                    new EmailTool(),
                    new LinuxArtifactsTool(),
                    new MemoryTool(),
                    new NetworkTool(),
                    new MobileTool(),
                ],
            },
            new()
            {
                Title = "Analyze",
                Tools =
                [
                    new TimelineTool(),
                    new MapTool(),
                    new GraphTool(),
                    new SearchTool(),
                    new HashSetsTool(),
                    new YaraTool(),
                    new IocMatchTool(),
                    new VirusTotalTool(),
                    new AiCopilotTool(),
                ],
            },
            new()
            {
                Title = "Acquire",
                Tools =
                [
                    new ImagerTool(),
                    new VerifyTool(),
                    new MountTool(),
                    new ConvertTool(),
                    new ShadowCopyTool(),
                    new RamCaptureTool(),
                    new CarverTool(),
                    new CloudPullTool(),
                ],
            },
            new()
            {
                Title = "Case",
                Tools =
                [
                    new CasesTool(Dashboard),
                    new BookmarksTool(),
                    new ReportsTool(),
                    new CustodyTool(),
                    new WorkflowsTool(),
                    new PluginsTool(),
                    new SettingsTool(),
                ],
            },
        ];

        // Group each section by Phase (ascending). Tools with no phase fall to the bottom,
        // preserving their original declaration order within each phase bucket. Hex stays at the
        // top of Examine because it's Phase 1.
        foreach (var section in Sections)
        {
            SortByPhase(section.Tools);
        }

        // Rail: Home is a lone item at the top and Settings is pinned at the foot, so neither
        // gets a group header. Examine (where almost every session starts) opens by default.
        RailSections = new ObservableCollection<ToolSection>(Sections.Where(s => s.Title != "Home"));
        SettingsEntry = Sections.SelectMany(s => s.Tools).First(t => t.Kind == "settings");
        ApplyRailFilter();

        // Home / Dashboard is selected by default — beginners never land on a blank parser.
        SelectedTool = Dashboard;
        ApplySelection(SelectedTool);
    }

    private static void SortByPhase(ObservableCollection<ToolViewModel> tools)
    {
        // Stable sort: pair each tool with its original index, sort by (phase, index), then rewrite.
        var ordered = tools
            .Select((tool, index) => (tool, index, phase: ParsePhase(tool.Phase)))
            .OrderBy(t => t.phase)
            .ThenBy(t => t.index)
            .Select(t => t.tool)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var currentIndex = tools.IndexOf(ordered[i]);
            if (currentIndex != i)
            {
                tools.Move(currentIndex, i);
            }
        }
    }

    private static int ParsePhase(string phase) =>
        int.TryParse(phase, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : int.MaxValue;

    partial void OnSelectedToolChanged(ToolViewModel? value)
    {
        ApplySelection(value);
        OnPropertyChanged(nameof(IsHexSelected));
        OnPropertyChanged(nameof(ShowToolHeader));
    }

    /// <summary>The hex viewer brings its own tab strip and toolbar, so the shell hides its header for it.</summary>
    public bool IsHexSelected => SelectedTool?.Kind == "hex";

    public bool ShowToolHeader => SelectedTool is not null && SelectedTool.Kind is not ("hex" or "dashboard");

    private void ApplySelection(ToolViewModel? value)
    {
        foreach (var s in Sections)
        {
            foreach (var t in s.Tools)
            {
                t.IsSelected = ReferenceEquals(t, value);
            }
            // Reveal the selection: a tool picked from the palette must not hide in a collapsed group.
            if (value is not null && s.Tools.Contains(value))
            {
                // Home and Settings sit outside the groups, so they have nothing to open.
                if (RailSections?.Contains(s) == true)
                {
                    _expanded.Add(s.Title);
                }
                s.IsExpanded = true;
            }
        }
    }

    // ============================ Rail ============================

    /// <summary>The collapsible groups (Examine / Analyze / Acquire / Case).</summary>
    public ObservableCollection<ToolSection> RailSections { get; }

    /// <summary>
    /// The groups the examiner has open, by title. Kept apart from <see cref="ToolSection.IsExpanded"/>
    /// because filtering opens every group to show its matches: without a separate record,
    /// clearing the filter left everything open and the examiner's own arrangement was lost.
    /// Examine (where almost every session starts) is open by default.
    /// </summary>
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal) { "Examine" };

    /// <summary>Titles of the open groups, for saving with the window layout.</summary>
    public IReadOnlyCollection<string> ExpandedSections => _expanded;

    /// <summary>Restores a saved arrangement. The group holding the selected tool always stays open.</summary>
    public void RestoreExpandedSections(IEnumerable<string>? titles)
    {
        if (titles is null)
        {
            return;
        }
        _expanded.Clear();
        foreach (var t in titles)
        {
            if (RailSections.Any(s => s.Title == t))
            {
                _expanded.Add(t);
            }
        }
        if (SelectedTool is { } selected && RailSections.FirstOrDefault(s => s.Tools.Contains(selected)) is { } home)
        {
            _expanded.Add(home.Title);
        }
        ApplyRailFilter();
    }

    /// <summary>Settings, pinned at the foot of the rail.</summary>
    public ToolViewModel SettingsEntry { get; }

    [ObservableProperty]
    private string? _railFilter;

    /// <summary>False when the filter hides every tool — the rail shows "No tools match".</summary>
    [ObservableProperty]
    private bool _hasRailMatches = true;

    partial void OnRailFilterChanged(string? value) => ApplyRailFilter();

    private void ApplyRailFilter()
    {
        var filtering = !string.IsNullOrWhiteSpace(RailFilter);
        foreach (var s in RailSections)
        {
            s.ApplyFilter(RailFilter);
            // While filtering every group shows its matches; afterwards the examiner's own
            // arrangement comes back.
            s.IsExpanded = filtering || _expanded.Contains(s.Title);
        }
        HasRailMatches = RailSections.Any(s => !s.IsEmpty);
    }

    [RelayCommand]
    private void ToggleSection(ToolSection? section)
    {
        if (section is not null)
        {
            section.IsExpanded = !section.IsExpanded;
            if (section.IsExpanded)
            {
                _expanded.Add(section.Title);
            }
            else
            {
                _expanded.Remove(section.Title);
            }
        }
    }

    /// <summary>Enter in the rail filter opens the first match.</summary>
    [RelayCommand]
    private void SelectFirstMatch()
    {
        var first = RailSections.SelectMany(s => s.VisibleTools).FirstOrDefault();
        if (first is not null)
        {
            SelectedTool = first;
        }
    }

    [RelayCommand]
    private void Select(ToolViewModel? tool) => SelectedTool = tool;

    public ToolViewModel? FindByKind(string kind)
    {
        foreach (var s in Sections)
        {
            foreach (var t in s.Tools)
            {
                if (t.Kind == kind)
                {
                    return t;
                }
            }
        }
        return null;
    }
}
