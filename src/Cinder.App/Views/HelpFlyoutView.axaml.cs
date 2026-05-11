using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cinder.App.ViewModels;

namespace Cinder.App.Views;

/// <summary>
/// The "?" help flyout. Content is rendered entirely through XAML data binding against
/// the selected tool's <c>HelpBlocks</c> property; the code-behind is just for the
/// Esc-to-close affordance and giving the overlay keyboard focus when it appears.
/// </summary>
public sealed partial class HelpFlyoutView : UserControl
{
    public HelpFlyoutView()
    {
        InitializeComponent();
        PropertyChanged += OnAttachedPropertyChanged;
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAttachedPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // When the flyout becomes visible, claim focus so Esc reaches us.
        if (e.Property == IsVisibleProperty && IsVisible)
        {
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
}
