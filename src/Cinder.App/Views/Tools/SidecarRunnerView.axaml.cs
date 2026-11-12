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
            // No drop while a read is in progress: it would replace the run the examiner is waiting on.
            var busy = DataContext is SidecarToolViewModel { IsLoading: true };
            e.DragEffects = !busy && e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Starts a read, unless one is already running. The buttons are disabled while a read runs;
    /// this guard covers the paths a disabled button does not (a drop, a dialog opened earlier).
    /// The view-model also supersedes safely if two runs ever do overlap.
    /// </summary>
    private static Task LoadAsync(SidecarToolViewModel vm, string? path)
    {
        if (string.IsNullOrEmpty(path) || vm.IsLoading)
        {
            return Task.CompletedTask;
        }
        return vm.LoadEvidenceCommand.ExecuteAsync(path);
    }

    // IDE1006 (the repo-wide "async methods end in Async" rule) is suppressed here rather than
    // satisfied. These are `async void` XAML event handlers: nothing can await them, so an Async
    // suffix would advertise a contract the method doesn't have.
#pragma warning disable IDE1006
    private async void OnPickFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SidecarToolViewModel vm || vm.IsLoading) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Pick a file for {vm.Title}",
            AllowMultiple = false,
        });
        // Picking is the intent to read — run straight away rather than asking for a second click.
        await LoadAsync(vm, picked.FirstOrDefault()?.TryGetLocalPath());
    }

    private async void OnPickFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SidecarToolViewModel vm || vm.IsLoading) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Pick a folder for {vm.Title}",
            AllowMultiple = false,
        });
        await LoadAsync(vm, picked.FirstOrDefault()?.TryGetLocalPath());
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not SidecarToolViewModel vm) return;
        // A dropped folder arrives here the same way a file does; the view-model says so if this
        // tool cannot read the kind of thing that was dropped.
        await LoadAsync(vm, e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath());
    }
#pragma warning restore IDE1006
}
