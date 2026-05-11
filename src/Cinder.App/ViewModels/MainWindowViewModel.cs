using System.Collections.ObjectModel;
using Avalonia.Threading;
using Cinder.App.Services;
using Cinder.App.ViewModels.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public CommandRegistry Commands { get; }
    public CommandPaletteViewModel Palette { get; }
    public WorkspaceViewModel Workspace { get; } = new();

    public ReportsToolViewModel Reports { get; }
    public TimelineToolViewModel Timeline { get; } = new();
    public SearchToolViewModel Search { get; } = new();
    public AiCopilotToolViewModel Ai { get; } = new();

    /// <summary>One <see cref="HexViewModel"/> per open file. <see cref="Hex"/> is the active one.</summary>
    public ObservableCollection<HexViewModel> OpenBuffers { get; } = new();

    [ObservableProperty]
    private HexViewModel _hex;

    public ObservableCollection<string> CaseTreeItems { get; } = ["No case open"];

    public ObservableCollection<string> ActivityLog { get; } = ["Cinder started."];

    [ObservableProperty]
    private string _activityHeadline = "Cinder started.";

    [ObservableProperty]
    private string _statusBar = "🛡 WriteBlock OFF · No case · Ctrl+K for the command palette";

    [ObservableProperty]
    private string _headerSubtitle = "Affluent Labs · pre-alpha";

    [ObservableProperty]
    private string? _activeCaseName;

    /// <summary>Whether the contextual "?" help flyout is open over the current tool.</summary>
    [ObservableProperty]
    private bool _isHelpOpen;

    /// <summary>
    /// All cases currently "open" in the shell (i.e. shown as tabs). The user can have several
    /// at once — useful for comparing two laptops in the same investigation, or for keeping a
    /// reference case open beside an active one.
    /// </summary>
    public ObservableCollection<CaseSession> OpenCases { get; } = new();

    [ObservableProperty]
    private CaseSession? _activeCase;

    public MainWindowViewModel(CommandRegistry commands)
    {
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _hex = new HexViewModel();
        OpenBuffers.Add(_hex);
        Palette = new CommandPaletteViewModel(commands);
        Reports = new ReportsToolViewModel(() => ActiveCaseName ?? "Untitled case");

        // Status-bar headline auto-fades to "Ready" after 3s if nothing else is happening.
        DispatcherTimer.RunOnce(() =>
        {
            if (ActivityHeadline == "Cinder started.")
            {
                ActivityHeadline = "Ready";
            }
        }, TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Open a file into a fresh hex buffer (a new tab). If the same path is already open the
    /// existing buffer is brought to focus instead. Also records the path on the Dashboard's
    /// "recent evidence" list so the user has a one-click path back to it.
    /// </summary>
    public void OpenFileInNewBuffer(string path)
    {
        var existing = OpenBuffers.FirstOrDefault(b =>
            string.Equals(b.Buffer?.DisplayName, System.IO.Path.GetFileName(path), StringComparison.Ordinal));
        if (existing is not null)
        {
            Hex = existing;
            Workspace.Dashboard.NoteEvidenceOpened(path);
            return;
        }

        // The very first slot starts empty (placeholder); reuse it.
        var slot = OpenBuffers.FirstOrDefault(b => b.Buffer is null);
        if (slot is null)
        {
            slot = new HexViewModel();
            OpenBuffers.Add(slot);
        }
        slot.OpenFile(path);
        Hex = slot;
        Workspace.Dashboard.NoteEvidenceOpened(path);
        Announce($"Opened {System.IO.Path.GetFileName(path)}");
    }

    [RelayCommand]
    private void SetActiveBuffer(HexViewModel? buffer)
    {
        if (buffer is not null && OpenBuffers.Contains(buffer))
        {
            Hex = buffer;
        }
    }

    [RelayCommand]
    private void CloseBuffer(HexViewModel? buffer)
    {
        if (buffer is null)
        {
            return;
        }
        if (OpenBuffers.Count <= 1)
        {
            // Last tab — empty it instead of removing.
            buffer.Dispose();
            return;
        }
        var idx = OpenBuffers.IndexOf(buffer);
        OpenBuffers.Remove(buffer);
        buffer.Dispose();
        if (Hex == buffer)
        {
            Hex = OpenBuffers[Math.Max(0, Math.Min(idx, OpenBuffers.Count - 1))];
        }
    }

    public void Announce(string headline)
    {
        ActivityHeadline = headline;
        ActivityLog.Insert(0, headline);
        DispatcherTimer.RunOnce(() =>
        {
            if (ActivityHeadline == headline)
            {
                ActivityHeadline = "Ready";
            }
        }, TimeSpan.FromSeconds(4));
    }

    partial void OnActiveCaseNameChanged(string? value)
    {
        HeaderSubtitle = string.IsNullOrEmpty(value) ? "Affluent Labs · pre-alpha" : value;
    }

    partial void OnActiveCaseChanged(CaseSession? value)
    {
        ActiveCaseName = value?.Name;
    }

    /// <summary>
    /// Opens (or re-activates if already open) a case in the top-of-window tab strip. Also
    /// records the case in the Dashboard's "recent cases" list so the user gets a one-click
    /// path back to it.
    /// </summary>
    public void OpenCase(Guid id, string name, string examiner, string? path)
    {
        var existing = OpenCases.FirstOrDefault(c => c.Id == id);
        if (existing is null)
        {
            existing = new CaseSession(id, name, examiner, path, DateTimeOffset.UtcNow);
            OpenCases.Add(existing);
        }
        ActiveCase = existing;
        Workspace.Dashboard.NoteCaseOpened(id, name, examiner);
    }

    [RelayCommand]
    private void SelectCase(CaseSession? session)
    {
        if (session is not null && OpenCases.Contains(session))
        {
            ActiveCase = session;
        }
    }

    [RelayCommand]
    private void CloseCase(CaseSession? session)
    {
        if (session is null)
        {
            return;
        }
        var idx = OpenCases.IndexOf(session);
        OpenCases.Remove(session);
        if (ActiveCase == session)
        {
            ActiveCase = OpenCases.Count == 0
                ? null
                : OpenCases[Math.Max(0, Math.Min(idx, OpenCases.Count - 1))];
        }
    }

    [RelayCommand]
    private void OpenPalette() => Palette.IsOpen = true;

    [RelayCommand]
    private async Task OpenFileAsync(CancellationToken ct)
    {
        var openCmd = Commands.Commands.FirstOrDefault(c => c.Id == "file.open");
        if (openCmd is not null)
        {
            await openCmd.Invoke(ct).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private void OpenFind()
    {
        // Surfaces the Find dialog through the command. The dialog itself is opened from
        // FindCommand.cs (registered in CommandRegistration.RegisterBuiltIns).
        var findCmd = Commands.Commands.FirstOrDefault(c => c.Id == "hex.find");
        findCmd?.Invoke(default);
    }

    [RelayCommand]
    private void OpenGoto()
    {
        var gotoCmd = Commands.Commands.FirstOrDefault(c => c.Id == "hex.goto");
        gotoCmd?.Invoke(default);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var cmd = Commands.Commands.FirstOrDefault(c => c.Id == "app.settings");
        cmd?.Invoke(default);
    }

    [RelayCommand]
    private async Task CreateCaseAsync(CancellationToken ct)
    {
        var cmd = Commands.Commands.FirstOrDefault(c => c.Id == "case.create");
        if (cmd is not null)
        {
            await cmd.Invoke(ct).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private void OpenHelp() => IsHelpOpen = true;

    [RelayCommand]
    private void CloseHelp() => IsHelpOpen = false;
}

/// <summary>
/// One open case in the top-of-window tab strip. Holds just enough to display and switch back
/// to — the actual case database is reopened on demand by services that need it.
/// </summary>
public sealed record CaseSession(
    Guid Id,
    string Name,
    string Examiner,
    string? Path,
    DateTimeOffset OpenedUtc);

