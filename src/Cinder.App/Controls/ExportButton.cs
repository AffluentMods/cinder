using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Cinder.App.Controls;

/// <summary>
/// The toolbar "Export" menu. <see cref="ExportCommand"/> is invoked with the chosen format
/// ("csv", "json", …) as its parameter. Built in code so the menu items do not depend on
/// binding across the flyout's popup boundary, and so every tool offers the same wording.
/// </summary>
public class ExportButton : Button
{
    public static readonly StyledProperty<ICommand?> ExportCommandProperty =
        AvaloniaProperty.Register<ExportButton, ICommand?>(nameof(ExportCommand));

    /// <summary>Comma-separated format ids, in menu order.</summary>
    public static readonly StyledProperty<string> FormatsProperty =
        AvaloniaProperty.Register<ExportButton, string>(nameof(Formats), "csv,json");

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csv"] = "CSV (opens in a spreadsheet)",
        ["json"] = "JSON",
        ["jsonl"] = "JSON Lines",
        ["kml"] = "KML (Google Earth)",
        ["bodyfile"] = "Bodyfile (mactime)",
        ["pdf"] = "PDF (archival PDF/A)",
        ["docx"] = "Word (DOCX)",
        ["html"] = "HTML",
        ["md"] = "Markdown",
    };

    protected override Type StyleKeyOverride => typeof(Button);

    public ExportButton()
    {
        Classes.Add("tb");
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new CinderIcon { Data = Icons.Download, Width = 14, Height = 14 },
                new TextBlock { Text = "Export", VerticalAlignment = VerticalAlignment.Center },
                new CinderIcon { Data = Icons.ChevronDown, Width = 12, Height = 12 },
            },
        };
        ToolTip.SetTip(this, "Save the rows shown here to a file");
        RebuildMenu();
    }

    public ICommand? ExportCommand
    {
        get => GetValue(ExportCommandProperty);
        set => SetValue(ExportCommandProperty, value);
    }

    public string Formats
    {
        get => GetValue(FormatsProperty);
        set => SetValue(FormatsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FormatsProperty || change.Property == ExportCommandProperty)
        {
            RebuildMenu();
        }
    }

    private void RebuildMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var format in Formats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            menu.Items.Add(new MenuItem
            {
                Header = Labels.GetValueOrDefault(format, format.ToUpperInvariant()),
                Command = ExportCommand,
                CommandParameter = format,
            });
        }
        Flyout = menu;
    }
}
