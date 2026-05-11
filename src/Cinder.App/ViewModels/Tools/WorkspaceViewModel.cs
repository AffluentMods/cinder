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
                    new CasesTool(),
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

    partial void OnSelectedToolChanged(ToolViewModel? value) => ApplySelection(value);

    private void ApplySelection(ToolViewModel? value)
    {
        foreach (var s in Sections)
        {
            foreach (var t in s.Tools)
            {
                t.IsSelected = ReferenceEquals(t, value);
            }
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
