using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cinder.App.ViewModels;

namespace Cinder.App.Views;

public partial class GotoDialog : Window
{
    public GotoDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            this.FindControl<TextBox>("GotoInput")?.Focus();
        };
        // Close after submit too — we listen on the GotoCommand by hooking the submit key.
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is HexViewModel vm)
        {
            var input = this.FindControl<TextBox>("GotoInput")?.Text ?? "";
            vm.GotoOffsetCommand.Execute(input);
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
