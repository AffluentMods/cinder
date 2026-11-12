using System.Text.Json;

namespace Cinder.App.Services;

/// <summary>Where the window was and how its panes were sized when it last closed.</summary>
public sealed record WindowLayout(
    double X,
    double Y,
    double Width,
    double Height,
    bool Maximized,
    double RailWidth,
    double InspectorWidth,
    bool RailOpen,
    bool InspectorOpen);

/// <summary>
/// Persists <see cref="WindowLayout"/> to <c>&lt;LocalAppData&gt;/Cinder/window.json</c>. Kept out of
/// settings.json on purpose: the Settings dialog rewrites that record wholesale on save, and
/// would silently drop anything it doesn't know about.
/// </summary>
public sealed class WindowLayoutStore(string? path = null)
{
    private readonly string _path = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cinder", "window.json");

    public WindowLayout? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }
            var layout = JsonSerializer.Deserialize<WindowLayout>(File.ReadAllText(_path));
            // Reject anything implausible (a corrupt file, or a monitor that no longer exists
            // is handled by the caller clamping to the visible screen).
            return layout is { Width: >= 800 and <= 16384, Height: >= 500 and <= 16384 } ? layout : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(WindowLayout layout)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(layout));
        }
        catch
        {
            // Best effort: failing to remember a window size must never block closing.
        }
    }
}
