using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cinder.App.ViewModels;

namespace Cinder.App.Services;

/// <summary>
/// Development harness for visual review: renders the real main window to a PNG and exits.
/// Nothing here runs unless <c>--shot</c> is on the command line.
///
/// <code>
///   Cinder.exe --shot out.png [--tool hex] [--open file] [--caret 0x20] [--select 0x20:0x2F]
///              [--size 1580x900] [--delay 1500] [--light] [--case "Demo case"]
/// </code>
///
/// The window is rendered through <see cref="RenderTargetBitmap"/>, i.e. the same visual tree
/// the user sees, at 1× scale — so layout, tokens and templates are what get checked.
/// </summary>
public static class DevScreenshot
{
    public static bool TryAttach(string[] args, Window window, MainWindowViewModel vm)
    {
        var shot = Arg(args, "--shot");
        if (shot is null)
        {
            return false;
        }

        if (Arg(args, "--size") is { } size && size.Split('x') is [var w, var h]
            && double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var ww)
            && double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var hh))
        {
            window.Width = ww;
            window.Height = hh;
        }
        if (args.Contains("--light"))
        {
            Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        }

        var delay = int.TryParse(Arg(args, "--delay"), out var d) ? d : 1500;

        window.Opened += async (_, _) =>
        {
            try
            {
                if (Arg(args, "--case") is { } caseName)
                {
                    vm.OpenCase(Guid.NewGuid(), caseName, Environment.UserName, null);
                }
                if (Arg(args, "--open") is { } file && File.Exists(file))
                {
                    vm.OpenFileInNewBuffer(Path.GetFullPath(file));
                }
                if (Arg(args, "--tool") is { } kind && vm.Workspace.FindByKind(kind) is { } tool)
                {
                    vm.Workspace.SelectedTool = tool;
                }
                if (Arg(args, "--evidence") is { } ev && vm.Workspace.SelectedTool is Cinder.App.ViewModels.Tools.SidecarToolViewModel st)
                {
                    st.EvidencePath = Path.GetFullPath(ev);
                    await st.LoadEvidenceCommand.ExecuteAsync(st.EvidencePath);
                }
                if (Arg(args, "--filter") is { } filter)
                {
                    vm.Workspace.RailFilter = filter;
                }
                if (args.Contains("--no-rail"))
                {
                    vm.IsRailOpen = false;
                }
                if (Arg(args, "--caret") is { } caret && ParseOffset(caret) is { } co)
                {
                    vm.Hex.JumpTo(co);
                }
                if (Arg(args, "--select") is { } sel && sel.Split(':') is [var a, var b]
                    && ParseOffset(a) is { } sa && ParseOffset(b) is { } sb)
                {
                    vm.Hex.SelectionStart = sa;
                    vm.Hex.SelectionEnd = sb;
                    vm.Hex.CaretOffset = sb;
                }
                if (args.Contains("--palette"))
                {
                    vm.Palette.IsOpen = true;
                }
                if (args.Contains("--help-open"))
                {
                    vm.IsHelpOpen = true;
                }

                await Task.Delay(delay);
                await Dispatcher.UIThread.InvokeAsync(() => Render(window, shot), DispatcherPriority.Background);
                if (args.Contains("--diag"))
                {
                    var diag = new System.Text.StringBuilder();
                    foreach (var g in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<DataGrid>())
                    {
                        var items = g.ItemsSource is System.Collections.ICollection col ? col.Count : -1;
                        diag.AppendLine($"DataGrid visible={g.IsEffectivelyVisible} bounds={g.Bounds} columns={g.Columns.Count} items={items} auto={g.AutoGenerateColumns}");
                        if (g.ItemsSource is System.Collections.IEnumerable en)
                        {
                            foreach (var first in en) { diag.AppendLine("  first item type: " + first?.GetType().FullName); break; }
                        }
                    }
                    await File.WriteAllTextAsync(shot + ".diag.txt", diag.ToString());
                }
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(shot + ".error.txt", ex.ToString());
            }
            finally
            {
                window.Close();
            }
        };
        return true;
    }

    private static void Render(Window window, string path)
    {
        var size = new PixelSize((int)Math.Ceiling(window.ClientSize.Width), (int)Math.Ceiling(window.ClientSize.Height));
        using var bmp = new RenderTargetBitmap(size, new Vector(96, 96));
        bmp.Render(window);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path);
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static long? ParseOffset(string s)
    {
        s = s.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null
            : long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
