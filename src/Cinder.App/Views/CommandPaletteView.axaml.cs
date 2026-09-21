using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Transformation;
using Cinder.App.Services;
using Cinder.App.ViewModels;

namespace Cinder.App.Views;

public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                OnVisibilityChanged(IsVisible);
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CommandPaletteViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    // Keep the highlighted row on screen while arrowing through a long list.
                    if (e.PropertyName == nameof(CommandPaletteViewModel.Selected) && vm.Selected is { } sel)
                    {
                        this.FindControl<ListBox>("List")?.ScrollIntoView(sel);
                    }
                };
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Opens with a short scale-and-fade from the top edge (0.97 → 1), and puts the caret in the
    /// query box so typing starts immediately.
    /// </summary>
    private void OnVisibilityChanged(bool visible)
    {
        var panel = this.FindControl<Border>("Panel");
        if (panel is null)
        {
            return;
        }
        if (visible)
        {
            panel.Opacity = 0;
            panel.RenderTransform = TransformOperations.Parse("scale(0.97)");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                panel.Opacity = 1;
                panel.RenderTransform = TransformOperations.Parse("scale(1)");
                this.FindControl<TextBox>("QueryBox")?.Focus();
            }, Avalonia.Threading.DispatcherPriority.Render);
        }
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is CommandPaletteViewModel vm)
        {
            vm.IsOpen = false;
        }
    }

    private void OnItemActivated(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is CommandPaletteViewModel vm && sender is Control { DataContext: CommandDescriptor cmd })
        {
            vm.InvokeCommand.Execute(cmd);
        }
    }
}
