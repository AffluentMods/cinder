using System.Collections.ObjectModel;
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

    public WorkspaceViewModel()
    {
        Sections =
        [
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

        // Hex selected by default.
        SelectedTool = Sections[0].Tools[0];
        ApplySelection(SelectedTool);
    }

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
