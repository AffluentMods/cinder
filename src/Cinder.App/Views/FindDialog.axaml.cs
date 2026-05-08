using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Cinder.App.Views;

public partial class FindDialog : Window
{
    public FindDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<TextBox>("QueryInput")?.Focus();
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
