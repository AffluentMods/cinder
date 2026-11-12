using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Cinder.App.ViewModels.Tools;

namespace Cinder.App.Views.Tools;

public partial class SidecarRunnerView : UserControl
{
    public SidecarRunnerView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // IDE1006 (the repo-wide "async methods end in Async" rule) is suppressed here rather than
    // satisfied. These are `async void` XAML event handlers: nothing can await them, so an Async
    // suffix would advertise a contract the method doesn't have.
#pragma warning disable IDE1006
    private async void OnPickEvidenceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SidecarToolViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Pick evidence for {vm.Title}",
            AllowMultiple = false,
        });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        // Picking is the intent to parse — run straight away rather than asking for a second click.
        await vm.LoadEvidenceCommand.ExecuteAsync(path);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not SidecarToolViewModel vm) return;
        var path = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            await vm.LoadEvidenceCommand.ExecuteAsync(path);
        }
    }
#pragma warning restore IDE1006
}
