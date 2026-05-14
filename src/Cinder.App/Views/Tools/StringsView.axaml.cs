using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cinder.App.ViewModels;
using Cinder.App.ViewModels.Tools;

namespace Cinder.App.Views.Tools;

public partial class StringsView : UserControl
{
    public StringsView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Double-click on a strings row → jump the hex viewer to that offset. Wired in XAML via
    /// DataGrid.DoubleTapped. Self-contained — no extra view-model commands required.
    /// </summary>
    private void OnHitDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is not StringHit hit)
        {
            return;
        }
        if (DataContext is not StringsTool tool || string.IsNullOrEmpty(tool.Path))
        {
            return;
        }
        var window = this.FindAncestorOfType<Avalonia.Controls.Window>();
        if (window?.DataContext is MainWindowViewModel mvm)
        {
            mvm.JumpToOffset(tool.Path, hit.Offset);
        }
    }
}

/// <summary>Tiny visual-tree helper since AvaloniaObject doesn't expose this directly.</summary>
internal static class VisualTreeHelperExt
{
    public static T? FindAncestorOfType<T>(this Avalonia.Visual? self) where T : class
    {
        var v = self?.GetVisualParent();
        while (v is not null)
        {
            if (v is T match) return match;
            v = v.GetVisualParent();
        }
        return null;
    }

    private static Avalonia.Visual? GetVisualParent(this Avalonia.Visual v) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualParent(v);
}
