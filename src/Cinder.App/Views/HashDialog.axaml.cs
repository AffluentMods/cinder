using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Cinder.App.ViewModels;

namespace Cinder.App.Views;

/// <summary>
/// The "Hash a file…" dialog. Hashing starts on open when the dialog was given a path, a
/// dropped or browsed file starts it again, and closing the window stops a run in progress —
/// a multi-gigabyte read must not carry on behind a dialog that is no longer there.
/// </summary>
public partial class HashDialog : Window
{
    public HashDialog()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Opened += (_, _) =>
        {
            if (DataContext is HashDialogViewModel { Path: { Length: > 0 } } vm)
            {
                vm.HashCommand.Execute(null);
            }
            else
            {
                this.FindControl<TextBox>("PathBox")?.Focus();
            }
        };
        Closed += (_, _) => (DataContext as HashDialogViewModel)?.HashCancelCommand.Execute(null);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    // IDE1006 is suppressed for the same reason as elsewhere in Views: an `async void` event
    // handler has no awaitable contract for an Async suffix to advertise.
#pragma warning disable IDE1006
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose the file to hash",
                AllowMultiple = false,
            });
            Start(picked.FirstOrDefault()?.TryGetLocalPath());
        }
        catch (Exception ex)
        {
            // A picker that fails must not take the dialog (or the app) with it.
            if (DataContext is HashDialogViewModel vm)
            {
                vm.Error = "The file picker could not be opened: " + ex.Message;
            }
        }
    }
#pragma warning restore IDE1006

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        Start(e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath());
    }

    /// <summary>Hashes <paramref name="path"/> unless a run is already in progress. The command itself never throws.</summary>
    private void Start(string? path)
    {
        if (string.IsNullOrEmpty(path) || DataContext is not HashDialogViewModel { IsComputing: false } vm)
        {
            return;
        }
        vm.Path = path;
        vm.HashCommand.Execute(null);
    }
}
