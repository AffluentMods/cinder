using System.Text.Json;

namespace Cinder.App.Services;

public sealed record CinderSettings
{
    public string Theme { get; init; } = "Dark";              // "Dark" | "Light" | "System"
    public string Density { get; init; } = "Comfortable";     // "Comfortable" | "Compact"
    public bool VimModeInHex { get; init; } = false;
    public bool RespectReduceMotion { get; init; } = true;
    public bool CheckForUpdates { get; init; } = true;
    public string? PythonExecutable { get; init; }
    public string? ParsersDirectory { get; init; }
    public Dictionary<string, string> AiProvider { get; init; } = new();   // id, model, endpoint, …
    public Dictionary<string, string> CloudClientIds { get; init; } = new(); // provider → OAuth client id
    public List<string> EnabledPlugins { get; init; } = new();
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    public SettingsStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cinder", "settings.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
    }

    public CinderSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new CinderSettings();
        }
        try
        {
            return JsonSerializer.Deserialize<CinderSettings>(File.ReadAllText(_path)) ?? new CinderSettings();
        }
        catch
        {
            return new CinderSettings();
        }
    }

    public void Save(CinderSettings settings)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, Json));
    }

    public string Path => _path;
}
