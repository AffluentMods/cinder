using Cinder.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

public sealed partial class SettingsDialogViewModel : ViewModelBase
{
    private readonly SettingsStore _store;

    [ObservableProperty]
    private string _theme = "Dark";

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

    // Cloud client_ids
    [ObservableProperty] private string? _googleDriveClientId;
    [ObservableProperty] private string? _oneDriveClientId;
    [ObservableProperty] private string? _dropboxAppKey;

    public IReadOnlyList<string> ThemeOptions { get; } = ["Dark", "Light", "System"];
    public IReadOnlyList<string> DensityOptions { get; } = ["Comfortable", "Compact"];
    public IReadOnlyList<string> AiProviderOptions { get; } =
        ["disabled", "ollama", "lm-studio", "openai-compatible", "openai", "anthropic"];

    public SettingsDialogViewModel(SettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        var s = _store.Load();
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
        AiApiKey = s.AiProvider.GetValueOrDefault("api_key");
        GoogleDriveClientId = s.CloudClientIds.GetValueOrDefault("google-drive");
        OneDriveClientId = s.CloudClientIds.GetValueOrDefault("onedrive");
        DropboxAppKey = s.CloudClientIds.GetValueOrDefault("dropbox");
    }

    [RelayCommand]
    private void Save()
    {
        var ai = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = AiProviderId ?? "disabled",
        };
        if (!string.IsNullOrEmpty(AiEndpoint)) ai["endpoint"] = AiEndpoint;
        if (!string.IsNullOrEmpty(AiModel)) ai["model"] = AiModel;
        if (!string.IsNullOrEmpty(AiApiKey)) ai["api_key"] = AiApiKey;

        var cloud = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(GoogleDriveClientId)) cloud["google-drive"] = GoogleDriveClientId;
        if (!string.IsNullOrEmpty(OneDriveClientId)) cloud["onedrive"] = OneDriveClientId;
        if (!string.IsNullOrEmpty(DropboxAppKey)) cloud["dropbox"] = DropboxAppKey;

        _store.Save(new CinderSettings
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
        });
    }
}
