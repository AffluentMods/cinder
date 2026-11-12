using Cinder.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

public sealed partial class SettingsDialogViewModel : ViewModelBase
{
    private readonly SettingsStore _store;

    [ObservableProperty]
    private string _theme = "Dark";

    // Density, Vim mode, reduce-motion, update check, Python executable and parsers directory
    // are not on the dialog (nothing in the app reads them yet). They are still loaded and
    // written back, so saving from the dialog never strips them out of an existing file.
    [ObservableProperty]
    private string _density = "Comfortable";

    [ObservableProperty]
    private bool _vimModeInHex;

    [ObservableProperty]
    private bool _respectReduceMotion = true;

    [ObservableProperty]
    private bool _checkForUpdates = true;

    [ObservableProperty]
    private string? _pythonExecutable;

    [ObservableProperty]
    private string? _parsersDirectory;

    // BYOM AI
    [ObservableProperty] private string _aiProviderId = "disabled";
    [ObservableProperty] private string? _aiEndpoint;
    [ObservableProperty] private string? _aiModel;
    [ObservableProperty] private string? _aiApiKey;

    // Hash sets (NSRL) — shared by the Hash sets tool and the Filesystem walk.
    [ObservableProperty] private string? _hashSetDatabase;
    [ObservableProperty] private string? _timestampAuthorityUrl;
    [ObservableProperty] private bool _hashFilesOnEnumerate;
    [ObservableProperty] private int _hashSizeLimitMb = 64;

    // Cloud client_ids
    [ObservableProperty] private string? _googleDriveClientId;
    [ObservableProperty] private string? _oneDriveClientId;
    [ObservableProperty] private string? _dropboxAppKey;

    /// <summary>
    /// Why the last <see cref="SaveCommand"/> did not save, or — straight after opening — why
    /// the settings file could not be read. Null when there is nothing wrong. The view can
    /// bind this, and should keep the dialog open while it is set.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private string? _saveError;

    public bool HasSaveError => !string.IsNullOrEmpty(SaveError);

    public IReadOnlyList<string> ThemeOptions { get; } = ["Dark", "Light", "System"];
    public IReadOnlyList<string> DensityOptions { get; } = ["Comfortable", "Compact"];
    public IReadOnlyList<string> AiProviderOptions { get; } =
        ["disabled", "ollama", "lm-studio", "openai-compatible", "openai", "mistral"];

    public SettingsDialogViewModel(SettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (!_store.TryLoad(out var s, out var error))
        {
            // Shown as defaults, but Save will refuse to write them over the unreadable file.
            SaveError = $"The settings file could not be read ({error}). It will not be overwritten.";
        }
        Theme = s.Theme;
        Density = s.Density;
        VimModeInHex = s.VimModeInHex;
        RespectReduceMotion = s.RespectReduceMotion;
        CheckForUpdates = s.CheckForUpdates;
        PythonExecutable = s.PythonExecutable;
        ParsersDirectory = s.ParsersDirectory;
        AiProviderId = s.AiProvider.GetValueOrDefault("id", "disabled");
        AiEndpoint = s.AiProvider.GetValueOrDefault("endpoint");
        AiModel = s.AiProvider.GetValueOrDefault("model");
        // The store presents the key under one spelling whichever one the file used.
        AiApiKey = s.AiProvider.GetValueOrDefault(SettingsStore.ApiKeyName);
        GoogleDriveClientId = s.CloudClientIds.GetValueOrDefault("google-drive");
        OneDriveClientId = s.CloudClientIds.GetValueOrDefault("onedrive");
        DropboxAppKey = s.CloudClientIds.GetValueOrDefault("dropbox");
        HashSetDatabase = s.HashSetDatabase;
        HashFilesOnEnumerate = s.HashFilesOnEnumerate;
        HashSizeLimitMb = s.HashSizeLimitMb;
        TimestampAuthorityUrl = s.TimestampAuthorityUrl;
    }

    /// <summary>
    /// Saves and applies the settings. Never throws: a failure (disk full, file locked, or a
    /// settings file that exists but cannot be read) is put in <see cref="SaveError"/> and
    /// shown in the shell's status bar, because the dialog that called this may close at once.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        try
        {
            // Start from what is on disk now, not from what was there when the dialog opened:
            // anything this dialog does not edit (enabled plug-ins, a hash-set database picked
            // in its own tool meanwhile, keys added by a newer build) is carried through.
            if (!_store.TryLoad(out var current, out var error))
            {
                throw new SettingsUnreadableException(_store.Path, error ?? "unknown error");
            }

            var ai = new Dictionary<string, string>(current.AiProvider, StringComparer.Ordinal);
            SetOrRemove(ai, "id", AiProviderId ?? "disabled");
            SetOrRemove(ai, "endpoint", AiEndpoint);
            SetOrRemove(ai, "model", AiModel);
            SetOrRemove(ai, SettingsStore.ApiKeyName, AiApiKey);

            var cloud = new Dictionary<string, string>(current.CloudClientIds, StringComparer.Ordinal);
            SetOrRemove(cloud, "google-drive", GoogleDriveClientId);
            SetOrRemove(cloud, "onedrive", OneDriveClientId);
            SetOrRemove(cloud, "dropbox", DropboxAppKey);

            _store.Save(current with
            {
                Theme = Theme,
                Density = Density,
                VimModeInHex = VimModeInHex,
                RespectReduceMotion = RespectReduceMotion,
                CheckForUpdates = CheckForUpdates,
                PythonExecutable = PythonExecutable,
                ParsersDirectory = ParsersDirectory,
                AiProvider = ai,
                CloudClientIds = cloud,
                HashSetDatabase = string.IsNullOrWhiteSpace(HashSetDatabase) ? null : HashSetDatabase,
                HashFilesOnEnumerate = HashFilesOnEnumerate,
                HashSizeLimitMb = Math.Clamp(HashSizeLimitMb, 1, 4096),
                TimestampAuthorityUrl = string.IsNullOrWhiteSpace(TimestampAuthorityUrl) ? null : TimestampAuthorityUrl.Trim(),
            });

            SaveError = null;
            ThemeService.Apply(Theme);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Settings were not saved");
            SaveError = "Settings were not saved: " + ex.Message;
            ShellNotice.Error(SaveError);
        }
    }

    private static void SetOrRemove(Dictionary<string, string> map, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            map.Remove(key);
        }
        else
        {
            map[key] = value;
        }
    }
}
