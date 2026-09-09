using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Cinder.App.ViewModels.Tools;

namespace Cinder.App.Views.Tools;

public partial class SidecarRunnerView : UserControl
{
    public SidecarRunnerView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // IDE1006 (the repo-wide "async methods end in Async" rule) is suppressed here rather than
    // satisfied. This is an `async void` XAML event handler: nothing can await it, so an Async
    // suffix would advertise a contract the method doesn't have. The convention exists for
    // methods a caller might await, and event handlers are the standard exception to it.
#pragma warning disable IDE1006
    private async void OnPickEvidenceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
#pragma warning restore IDE1006
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
        vm.EvidencePath = path;
    }
}
