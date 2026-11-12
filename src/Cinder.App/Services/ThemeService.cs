using Avalonia;
using Avalonia.Styling;

namespace Cinder.App.Services;

/// <summary>
/// Maps the persisted <see cref="CinderSettings.Theme"/> name ("Dark" / "Light" / "System") to
/// an Avalonia theme variant and back, and applies it to the running application.
/// </summary>
public static class ThemeService
{
    public const string DarkName = "Dark";
    public const string LightName = "Light";
    public const string SystemName = "System";

    /// <summary>Unknown or missing names mean Dark, the theme the app ships with.</summary>
    public static ThemeVariant Resolve(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        "LIGHT" => ThemeVariant.Light,
        "SYSTEM" or "DEFAULT" => ThemeVariant.Default,
        _ => ThemeVariant.Dark,
    };

    public static string NameOf(ThemeVariant? variant) =>
        variant == ThemeVariant.Light ? LightName
        : variant == ThemeVariant.Default ? SystemName
        : DarkName;

    /// <summary>Applies the named theme. A no-op when there is no running application (tests).</summary>
    public static void Apply(string? name)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = Resolve(name);
        }
    }

    /// <summary>
    /// Flips between dark and light (from whatever is showing now, so "System" toggles to the
    /// opposite of what the OS chose) and returns the name of the theme now in effect.
    /// </summary>
    public static string? Toggle()
    {
        if (Application.Current is not { } app)
        {
            return null;
        }
        var next = app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        app.RequestedThemeVariant = next;
        return NameOf(next);
    }
}
