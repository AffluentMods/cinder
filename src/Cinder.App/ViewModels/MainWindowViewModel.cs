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

    partial void OnHexChanged(HexViewModel? oldValue, HexViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActiveTab = false;
        }
        newValue.IsActiveTab = true;
    }

    /// <summary>Raised when Ctrl+F should put the caret in the hex viewer's find box.</summary>
    public event EventHandler? FocusFindRequested;

    [RelayCommand]
    private void FocusFind()
    {
        ShowHex();
        FocusFindRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowHex()
    {
        if (Workspace.FindByKind("hex") is { } hex)
        {
            Workspace.SelectedTool = hex;
        }
    }

    /// <summary>Hand the open file to the Strings tool and run it.</summary>
    [RelayCommand]
    private async Task OpenInStringsAsync(CancellationToken ct)
    {
        if (Hex.SourcePath is not { } path || Workspace.FindByKind("strings") is not StringsTool strings)
        {
            return;
        }
        Workspace.SelectedTool = strings;
        strings.Path = path;
        await strings.RunCommand.ExecuteAsync(null);
    }

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

    // ============================ Shell layout ============================

    /// <summary>Left rail visible (Ctrl+B).</summary>
    [ObservableProperty]
    private bool _isRailOpen = true;

    /// <summary>Inspector pane requested (Ctrl+Shift+B). It only ever shows for the hex viewer.</summary>
    [ObservableProperty]
    private bool _isInspectorOpen = true;

    public bool ShowInspector => IsInspectorOpen && Workspace.IsHexSelected;

    partial void OnIsInspectorOpenChanged(bool value) => OnPropertyChanged(nameof(ShowInspector));

    [RelayCommand]
    private void ToggleRail() => IsRailOpen = !IsRailOpen;

    [RelayCommand]
    private void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;

    /// <summary>The case tab strip only earns its row when there is more than one case to switch between.</summary>
    public bool HasMultipleCases => OpenCases.Count > 1;

    public bool HasActiveCase => !string.IsNullOrEmpty(ActiveCaseName);

    public string CaseCrumb => ActiveCaseName ?? "No case";

    public MainWindowViewModel(CommandRegistry commands)
    {
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _hex = new HexViewModel { IsActiveTab = true };
        OpenBuffers.Add(_hex);
        Palette = new CommandPaletteViewModel(commands);
        // Tools reach the hex viewer through this: "open this search hit / carved file / match".
        AppNavigator.CreateCase = () => CreateCaseCommand.ExecuteAsync(null);
        AppNavigator.OpenCase = () => OpenCaseFromPickerCommand.ExecuteAsync(null);
        AppNavigator.OpenCaseAtPath = OpenCaseFromPathAsync;
        AppNavigator.CloseActiveCase = () => CloseCaseCommand.Execute(ActiveCase);
        AppNavigator.OpenInHex = (path, offset) =>
        {
            if (!OpenFileInNewBuffer(path))
            {
                return;
            }
            ShowHex();
            if (offset is { } o && Hex.Buffer is not null)
            {
                Hex.JumpTo(o);
            }
        };
        Reports = new ReportsToolViewModel(() => ActiveCaseName ?? "Untitled case");

        OpenCases.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMultipleCases));
        Workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceViewModel.SelectedTool))
            {
                OnPropertyChanged(nameof(ShowInspector));
            }
        };

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
    ///
    /// <para>Returns false, with the reason announced, when the file cannot be opened. Evidence
    /// that is in use by another process, unreadable to this account, or not a file at all is
    /// routine on a live system; none of it may throw out of here, because the callers are
    /// dispatcher callbacks and command handlers where an escaped exception ends the session.</para>
    /// </summary>
    public bool OpenFileInNewBuffer(string path)
    {
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            AnnounceError($"Could not open {path}: {ex.Message}");
            return false;
        }

        // Match on the full path, never the file name: two users' NTUSER.DAT, or the same hive
        // from two snapshots, share a name and are different evidence.
        var existing = OpenBuffers.FirstOrDefault(b =>
            b.Buffer is not null && b.SourcePath is not null && string.Equals(b.SourcePath, full,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (existing is not null)
        {
            Hex = existing;
            Workspace.Dashboard.NoteEvidenceOpened(path);
            return true;
        }

        // The very first slot starts empty (placeholder); reuse it.
        var slot = OpenBuffers.FirstOrDefault(b => b.Buffer is null);
        var added = slot is null;
        slot ??= new HexViewModel();
        try
        {
            // OpenFile builds the new buffer before it changes anything, so a failure here
            // leaves the slot (and every other tab) exactly as it was.
            slot.OpenFile(full);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open {Path} in the hex viewer", full);
            AnnounceError($"Could not open {System.IO.Path.GetFileName(full)}: {DescribeOpenFailure(ex, full)}");
            return false;
        }
        if (added)
        {
            OpenBuffers.Add(slot);
        }
        Hex = slot;
        Workspace.Dashboard.NoteEvidenceOpened(path);
        Announce($"Opened {System.IO.Path.GetFileName(path)}");
        return true;
    }

    /// <summary>
    /// The reason a file would not open, short enough to read in the status bar. The framework's
    /// own messages repeat the full path, which pushes the reason itself out of view.
    /// </summary>
    internal static string DescribeOpenFailure(Exception ex, string path)
    {
        const int SharingViolation = unchecked((int)0x80070020);
        const int LockViolation = unchecked((int)0x80070021);
        return ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => "it no longer exists.",
            UnauthorizedAccessException when Directory.Exists(path) => "it is a folder, not a file.",
            UnauthorizedAccessException => "access was denied.",
            IOException { HResult: SharingViolation or LockViolation } =>
                "it is in use by another program. Close that program, or work on a copy.",
            _ => ex.Message,
        };
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

    /// <summary>The activity log keeps this many lines; a session left open for days must not grow it without bound.</summary>
    private const int ActivityLogLimit = 500;

    public void Announce(string headline) => Announce(headline, TimeSpan.FromSeconds(4));

    /// <summary>
    /// Announce something that went wrong. Stays in the status bar long enough to be read; an
    /// ordinary announcement replaces it as usual.
    /// </summary>
    public void AnnounceError(string headline) => Announce(headline, TimeSpan.FromSeconds(20));

    /// <summary>
    /// Announce something worth reading in full that is not an error (an available update and
    /// where to get it). Stays up for a minute; the status bar tooltip shows the whole text.
    /// </summary>
    public void AnnounceNotice(string headline) => Announce(headline, TimeSpan.FromSeconds(60));

    private void Announce(string headline, TimeSpan visibleFor)
    {
        // Callable from any thread: continuations that left the UI context (ConfigureAwait(false))
        // report failures through here, and the log is a bound collection.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Announce(headline, visibleFor));
            return;
        }
        ActivityHeadline = headline;
        ActivityLog.Insert(0, headline);
        while (ActivityLog.Count > ActivityLogLimit)
        {
            ActivityLog.RemoveAt(ActivityLog.Count - 1);
        }
        DispatcherTimer.RunOnce(() =>
        {
            if (ActivityHeadline == headline)
            {
                ActivityHeadline = "Ready";
            }
        }, visibleFor);
    }

    partial void OnActiveCaseNameChanged(string? value)
    {
        HeaderSubtitle = string.IsNullOrEmpty(value) ? "Affluent Labs · pre-alpha" : value;
        OnPropertyChanged(nameof(HasActiveCase));
        OnPropertyChanged(nameof(CaseCrumb));
    }

    partial void OnActiveCaseChanged(CaseSession? value)
    {
        ActiveCaseName = value?.Name;
        // Tools reach the custody log through this; it must track the active tab exactly.
        Services.ActiveCaseContext.Set(value);
    }

    /// <summary>
    /// Opens (or re-activates if already open) a case in the top-of-window tab strip. Also
    /// records the case in the Dashboard's "recent cases" list so the user gets a one-click
    /// path back to it.
    /// </summary>
    public void OpenCase(Guid id, string name, string examiner, string? path)
    {
        var existing = OpenCases.FirstOrDefault(c => c.Id == id);
        var newlyOpened = existing is null;
        if (existing is null)
        {
            existing = new CaseSession(id, name, examiner, path, DateTimeOffset.UtcNow);
            OpenCases.Add(existing);
        }
        ActiveCase = existing;
        Workspace.Dashboard.NoteCaseOpened(id, name, examiner, path);

        if (newlyOpened)
        {
            // Fire-and-forget by design: LogAsync never throws, and opening a case must not
            // wait on a database write.
            // Logged against this case by name, not "whichever case is active when the write runs".
            _ = Services.ActiveCaseContext.LogAsync(existing, Cinder.Core.Custody.CustodyAction.CaseOpened, new
            {
                Path = path,
                Machine = Environment.MachineName,
                CinderVersion = Services.UpdateChecker.CurrentVersion(),
            });
        }
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
            await openCmd.Invoke(ct);
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
            await cmd.Invoke(ct);
        }
    }

    [RelayCommand]
    private async Task OpenCaseFromPickerAsync(CancellationToken ct)
    {
        var cmd = Commands.Commands.FirstOrDefault(c => c.Id == "case.open");
        if (cmd is not null)
        {
            await cmd.Invoke(ct);
        }
    }

    [RelayCommand]
    private void OpenHelp() => IsHelpOpen = true;

    [RelayCommand]
    private void CloseHelp() => IsHelpOpen = false;

    /// <summary>
    /// Reopen a case the user already worked on. Skips the file picker — uses the path
    /// recorded with the recent-case entry. If the file is gone, OpenCaseFromPathAsync
    /// announces a friendly error.
    /// </summary>
    [RelayCommand]
    private async Task ReopenRecentCaseAsync(Tools.RecentCaseRow? row)
    {
        if (row is null || string.IsNullOrEmpty(row.Path))
        {
            return;
        }
        await OpenCaseFromPathAsync(row.Path);
    }

    /// <summary>
    /// Reopen a previously seen evidence file. Routes through OpenFileInNewBuffer so the Hex
    /// viewer becomes the active tool, mirroring what Ctrl+O does.
    /// </summary>
    [RelayCommand]
    private void ReopenRecentEvidence(Tools.RecentEvidenceRow? row)
    {
        if (row is null || string.IsNullOrEmpty(row.Path) || !File.Exists(row.Path))
        {
            return;
        }
        if (!OpenFileInNewBuffer(row.Path))
        {
            return;
        }
        var hexTool = Workspace.FindByKind("hex");
        if (hexTool is not null)
        {
            Workspace.SelectedTool = hexTool;
        }
    }

    /// <summary>
    /// Open <paramref name="path"/> in the hex viewer (reusing an existing buffer if it's already
    /// open) and jump the caret to <paramref name="offset"/>. Used by the Strings tool's "click
    /// a row to inspect that byte in hex" workflow.
    /// </summary>
    public void JumpToOffset(string path, long offset)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || offset < 0)
        {
            return;
        }
        if (!OpenFileInNewBuffer(path))
        {
            return;
        }
        var hexTool = Workspace.FindByKind("hex");
        if (hexTool is not null)
        {
            Workspace.SelectedTool = hexTool;
        }
        Hex.JumpTo(offset);
    }

    /// <summary>
    /// Open an existing .cinder case file by path. Reads the single case row, registers a
    /// session for it, and announces. Errors are swallowed into the activity headline so the
    /// app doesn't crash on a stale recent-cases entry.
    /// </summary>
    public async Task OpenCaseFromPathAsync(string casePath)
    {
        if (string.IsNullOrEmpty(casePath) || !File.Exists(casePath))
        {
            AnnounceError($"Case file not found: {System.IO.Path.GetFileName(casePath)}");
            return;
        }
        try
        {
            var store = new Cinder.Core.Cases.CaseStore(casePath);
            var custody = new Cinder.Core.Custody.CustodyLog(store);
            var svc = new Cinder.Core.Cases.CaseService(store, custody);
            var c = await svc.GetFirstAsync();
            if (c is null)
            {
                Announce($"No case row in {System.IO.Path.GetFileName(casePath)}");
                return;
            }
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                ActiveCaseName = c.Name;
                OpenCase(c.Id, c.Name, c.Examiner, casePath);
                Announce($"Case opened: {c.Name}");
            });
        }
        catch (Exception ex)
        {
            AnnounceError($"Failed to open case: {ex.Message}");
        }
    }
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

