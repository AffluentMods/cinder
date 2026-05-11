using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Cinder.App.ViewModels;
using Cinder.App.ViewModels.Tools;

namespace Cinder.App.Views;

/// <summary>
/// The "?" help flyout. Renders the active tool's HelpMarkdown into headings + body text
/// without pulling in a full markdown engine — we control the format, so a tiny parser is
/// enough and keeps the surface small.
/// </summary>
public sealed partial class HelpFlyoutView : UserControl
{
    public HelpFlyoutView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Refresh();
        PropertyChanged += OnAttachedPropertyChanged;
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAttachedPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && IsVisible)
        {
            Refresh();
            Focusable = true;
            _ = Focus();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel mvm)
        {
            mvm.IsHelpOpen = false;
            e.Handled = true;
        }
    }

    private void Refresh()
    {
        var body = this.FindControl<StackPanel>("HelpBody");
        if (body is null)
        {
            return;
        }

        body.Children.Clear();
        var tool = (DataContext as MainWindowViewModel)?.Workspace.SelectedTool;
        var text = tool?.HelpMarkdown ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            body.Children.Add(new TextBlock
            {
                Text = "(no help written yet)",
                Foreground = (IBrush)Application.Current!.Resources["CinderForegroundMutedBrush"]!,
            });
            return;
        }

        // Mini-markdown:
        //   "## Heading"       → section heading.
        //   "- bullet"         → bullet line.
        //   "1. item"          → numbered list item.
        //   blank line         → paragraph break (separates blocks).
        //   anything else      → paragraph body (collapsed across consecutive lines).
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }
            body.Children.Add(BuildParagraph(string.Join(" ", paragraph.Select(l => l.TrimEnd()))));
            paragraph.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushParagraph();
                body.Children.Add(BuildHeading(line[3..].Trim()));
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                body.Children.Add(BuildBullet(line[2..].Trim(), bullet: "•"));
                continue;
            }

            // Numbered list "1. ", "2. ", etc.
            var dot = line.IndexOf(". ", StringComparison.Ordinal);
            if (dot is > 0 and < 4 && int.TryParse(line[..dot], out _))
            {
                FlushParagraph();
                body.Children.Add(BuildBullet(line[(dot + 2)..].Trim(), bullet: line[..dot] + "."));
                continue;
            }

            paragraph.Add(line);
        }
        FlushParagraph();
    }

    private static TextBlock BuildHeading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Foreground = (IBrush)Application.Current!.Resources["CinderAccentBrush"]!,
        LetterSpacing = 1.4,
        Margin = new Thickness(0, 12, 0, 2),
    };

    private static TextBlock BuildParagraph(string text) => new()
    {
        Text = text,
        FontSize = 13,
        LineHeight = 19,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (IBrush)Application.Current!.Resources["CinderForegroundBrush"]!,
    };

    private static Grid BuildBullet(string text, string bullet)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            Margin = new Thickness(4, 1, 0, 1),
        };
        var marker = new TextBlock
        {
            Text = bullet,
            FontSize = 13,
            Foreground = (IBrush)Application.Current!.Resources["CinderForegroundMutedBrush"]!,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var body = new TextBlock
        {
            Text = text,
            FontSize = 13,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (IBrush)Application.Current!.Resources["CinderForegroundBrush"]!,
        };
        Grid.SetColumn(marker, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(marker);
        grid.Children.Add(body);
        return grid;
    }
}
