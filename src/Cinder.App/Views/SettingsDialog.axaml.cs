using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cinder.App.ViewModels;

namespace Cinder.App.Views;

public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    /// <summary>Save means "save and close": leaving the dialog open after Save made it unclear whether anything happened.</summary>
    private void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is SettingsDialogViewModel vm)
        {
            vm.SaveCommand.Execute(null);
            if (vm.HasSaveError)
            {
                // Stay open: the reason is shown above the buttons, and closing would lose the edits.
                return;
            }
        }
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
