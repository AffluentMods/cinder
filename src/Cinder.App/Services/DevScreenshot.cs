using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cinder.App.ViewModels;
using Cinder.App.ViewModels.Tools;
using Cinder.Core.Cases;
using Cinder.Core.Custody;

namespace Cinder.App.Services;

/// <summary>
/// Development harness for visual review: renders the real main window to a PNG and exits.
/// Nothing here runs unless <c>--shot</c> is on the command line.
///
/// <code>
///   Cinder.exe --shot out.png [--tool hex] [--open file] [--caret 0x20] [--select 0x20:0x2F]
///              [--size 1580x900] [--scale 2] [--delay 1500] [--light]
///              [--case "Demo case"] [--examiner name] [--case-file path]
///              [--seed-case path|name|examiner|evidencePath]
///              [--fresh-recents] [--recent-case "name|examiner|path"]… [--recent-evidence path]…
///              [--evidence file] [--row-filter text] [--hex-bookmark 0x200]…
///              [--report-demo] [--custody-verify] [--filter text] [--no-rail]
///              [--palette] [--help-open] [--dialog settings] [--diag] [--hold seconds]
/// </code>
///
/// The window is rendered through <see cref="RenderTargetBitmap"/>, i.e. the same visual tree
/// the user sees, so layout, tokens and templates are what get checked. The seeding options
/// exist so documentation screenshots can be produced from synthetic material only (see
/// docs/screenshots.md): nothing they create comes from a real case.
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
        if (window is Cinder.App.Views.MainWindow mw)
        {
            mw.PersistLayout = false;
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
        // --hold keeps the real window on screen after the render, so the on-screen result (the
        // compositor at the monitor's own scale) can be compared with the off-screen render.
        var hold = int.TryParse(Arg(args, "--hold"), out var hs) ? Math.Clamp(hs, 0, 120) : 0;
        if (hold == 0)
        {
            // A review run renders off-screen; do not flash a window over whatever the
            // developer is doing.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(-30000, -30000);
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
        }
        var scale = double.TryParse(Arg(args, "--scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) ? Math.Clamp(sc, 1, 4) : 1;
        var examiner = Arg(args, "--examiner") ?? Environment.UserName;

        window.Opened += async (_, _) =>
        {
            try
            {
                if (args.Contains("--fresh-recents"))
                {
                    vm.Workspace.Dashboard.ClearCommand.Execute(null);
                }
                foreach (var seed in ArgAll(args, "--seed-case"))
                {
                    await SeedCaseAsync(seed);
                }
                foreach (var rc in ArgAll(args, "--recent-case"))
                {
                    if (rc.Split('|') is [var name, var who, var path])
                    {
                        vm.Workspace.Dashboard.NoteCaseOpened(Guid.NewGuid(), name, who, path);
                    }
                }
                foreach (var re in ArgAll(args, "--recent-evidence"))
                {
                    vm.Workspace.Dashboard.NoteEvidenceOpened(re);
                }

                if (Arg(args, "--case-file") is { } caseFile)
                {
                    await vm.OpenCaseFromPathAsync(Path.GetFullPath(caseFile));
                }
                else if (Arg(args, "--case") is { } caseName)
                {
                    vm.OpenCase(Guid.NewGuid(), caseName, examiner, null);
                }
                if (Arg(args, "--open") is { } file && File.Exists(file))
                {
                    vm.OpenFileInNewBuffer(Path.GetFullPath(file));
                }
                if (Arg(args, "--tool") is { } kind && vm.Workspace.FindByKind(kind) is { } tool)
                {
                    vm.Workspace.SelectedTool = tool;
                }
                if (Arg(args, "--evidence") is { } ev && vm.Workspace.SelectedTool is SidecarToolViewModel st)
                {
                    st.EvidencePath = Path.GetFullPath(ev);
                    await st.LoadEvidenceCommand.ExecuteAsync(st.EvidencePath);
                    if (Arg(args, "--row-filter") is { } rowFilter)
                    {
                        st.RowFilter = rowFilter;
                    }
                }
                if (Arg(args, "--filter") is { } filter)
                {
                    vm.Workspace.RailFilter = filter;
                }
                if (args.Contains("--no-rail"))
                {
                    vm.IsRailOpen = false;
                }
                foreach (var mark in ArgAll(args, "--hex-bookmark"))
                {
                    if (ParseOffset(mark) is { } mo && vm.Hex.Buffer is not null)
                    {
                        vm.Hex.JumpTo(mo);
                        vm.Hex.AddBookmarkCommand.Execute(null);
                    }
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
                if (args.Contains("--report-demo"))
                {
                    await FillReportDemoAsync(vm, examiner);
                }
                if (args.Contains("--custody-verify") && vm.Workspace.FindByKind("custody") is CustodyTool custody)
                {
                    await custody.VerifyOpenCaseCommand.ExecuteAsync(null);
                }
                if (args.Contains("--palette"))
                {
                    vm.Palette.IsOpen = true;
                }
                if (args.Contains("--help-open"))
                {
                    vm.IsHelpOpen = true;
                }

                Window target = window;
                if (Arg(args, "--dialog") is { } dialog && CreateDialog(dialog) is { } dlg)
                {
                    dlg.Show(window);
                    target = dlg;
                }

                await Task.Delay(delay);
                await Dispatcher.UIThread.InvokeAsync(() => Render(target, shot, scale), DispatcherPriority.Background);
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
                if (hold > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(hold));
                }
                if (!ReferenceEquals(target, window))
                {
                    target.Close();
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

    /// <summary>
    /// The secondary windows, so they can be reviewed too. Settings is given a throwaway store:
    /// a screenshot run must never read or write the examiner's real settings file.
    /// </summary>
    private static Window? CreateDialog(string name)
    {
        switch (name)
        {
            case "settings":
                var path = Path.Combine(Path.GetTempPath(), "cinder-shot-settings.json");
                return new Cinder.App.Views.SettingsDialog { DataContext = new SettingsDialogViewModel(new SettingsStore(path)) };
            default:
                return null;
        }
    }

    /// <summary>
    /// Creates a case file with a few synthetic bookmarks: <c>path|name|examiner|evidencePath</c>.
    /// Refuses any path that is not inside a folder named "cinder-demo", because it replaces
    /// the file if it already exists.
    /// </summary>
    private static async Task SeedCaseAsync(string spec)
    {
        if (spec.Split('|') is not [var path, var name, var examiner, var evidence])
        {
            throw new ArgumentException("--seed-case expects path|name|examiner|evidencePath");
        }
        path = Path.GetFullPath(path);
        if (!path.Split(Path.DirectorySeparatorChar).Contains("cinder-demo"))
        {
            throw new InvalidOperationException("--seed-case only writes inside a folder named cinder-demo.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(f)) File.Delete(f);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var store = new CaseStore(path);
        var custody = new CustodyLog(store);
        var c = await new CaseService(store, custody).CreateAsync(name, examiner, null);
        var bookmarks = new BookmarkStore(store);
        await bookmarks.AddAsync(c.Id, examiner, "filesystem", evidence, "big.bin: 100 kB file with no extension match",
            "Largest user file on the volume. Content is high-entropy; carve and identify.",
            """{"Path":"big.bin","Size":"100000","Modified":"2026-09-09 20:34:27Z","IsDeleted":"False"}""");
        await bookmarks.AddAsync(c.Id, examiner, "filesystem", evidence, "f07.txt created in the same second as 39 other files",
            "Batch creation: all f00 to f19 share one timestamp, consistent with a scripted copy.",
            """{"Path":"f07.txt","Size":"1","Created":"2026-09-09 20:34:27Z","Modified":"2026-09-09 20:34:27Z"}""");
        await bookmarks.AddAsync(c.Id, examiner, "hex", evidence, "NTFS boot sector at offset 0x0",
            "OEM ID NTFS, 512 bytes per sector, 8 sectors per cluster.",
            """{"Offset":"0x000000000000","Length":"512","Signature":"EB 52 90 4E 54 46 53"}""");
        await custody.AppendAsync(c.Id, examiner, CustodyAction.ParserRan,
            $$"""{"Tool":"filesystem","Evidence":"{{evidence.Replace("\\", "\\\\")}}","Rows":39}""");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static async Task FillReportDemoAsync(MainWindowViewModel vm, string examiner)
    {
        var r = vm.Reports;
        r.Title = "Examination of removable media WKS-014-USB";
        r.Examiner = examiner;
        string[] bodies =
        [
            "I was instructed to examine one USB storage device and to report what user files it holds and when they were placed there.",
            "Eleven years in digital forensics. This report was prepared with Cinder; every step is recorded in the case custody log.",
            "One forensic image of the device, acquired through a hardware write blocker and verified against its acquisition hash before examination.",
            "The image was opened read-only. The file system was listed, file hashes were compared with a known-file set, and items of interest were bookmarked.",
            "The volume is NTFS and holds 39 entries. Twenty text files and one 100 kB binary file share a single creation second, which indicates a scripted copy rather than interactive use.",
        ];
        for (var i = 0; i < r.Sections.Count && i < bodies.Length; i++)
        {
            r.Sections[i].Body = bodies[i];
        }
        r.SelectedSection = r.Sections.Count > 4 ? r.Sections[4] : r.Sections.FirstOrDefault();
        await r.LoadBookmarksCommand.ExecuteAsync(null);
        r.RefreshCommand.Execute(null);
    }

    private static void Render(Window window, string path, double scale)
    {
        var size = new PixelSize(
            (int)Math.Ceiling(window.ClientSize.Width * scale),
            (int)Math.Ceiling(window.ClientSize.Height * scale));
        using var bmp = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bmp.Render(window);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path);
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static IEnumerable<string> ArgAll(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name)
            {
                yield return args[i + 1];
            }
        }
    }

    private static long? ParseOffset(string s)
    {
        s = s.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null
            : long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
