using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Cinder.App.ViewModels;

namespace Cinder.App.Views;

public partial class HexView : UserControl
{
    public HexView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        var first = files?.FirstOrDefault();
        var path = first?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            // Route through the main window so each drop spawns a new tab + auto-routes by type.
            var owner = TopLevel.GetTopLevel(this);
            if (owner is Window w && w.DataContext is MainWindowViewModel main)
            {
                main.OpenFileInNewBuffer(path);
            }
            else if (DataContext is HexViewModel vm)
            {
                vm.OpenFile(path);
            }
        }
        e.Handled = true;
    }
}
