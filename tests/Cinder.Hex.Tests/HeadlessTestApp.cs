using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Cinder.Hex.Tests;

[assembly: AvaloniaTestApplication(typeof(HeadlessTestApp))]

namespace Cinder.Hex.Tests;

/// <summary>
/// The application the headless test session runs: no display, no GPU, just the Avalonia
/// input, layout and render pipeline, with the Fluent theme so that a <c>Window</c> has a
/// template and can host a control. Tests marked <c>[AvaloniaFact]</c> run on its UI thread.
/// </summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
