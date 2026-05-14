using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels.Tools;

public sealed partial class GalleryTool : ToolViewModel
{
    public override string Id => "gallery";
    public override string Title => "Image gallery";
    public override string Icon => "🖼";
    public override string Subtitle => "Folder of images → thumbnails · EXIF · GPS · grouping.";
    public override string Phase => "1";
    public override string Kind => "gallery";

    public ObservableCollection<GalleryItem> Items { get; } = new();

    [ObservableProperty]
    private string? _folderPath;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusLine;

    private static readonly string[] Extensions =
    [
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".avif", ".tiff", ".tif", ".ico",
    ];

    [RelayCommand]
    private async Task PickFolderAsync(CancellationToken ct)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick a folder of images",
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        FolderPath = path;
        await LoadAsync(path, ct);
    }

    private async Task LoadAsync(string folder, CancellationToken ct)
    {
        IsLoading = true;
        StatusLine = "Scanning…";
        Items.Clear();
        try
        {
            var paths = await Task.Run(() =>
            {
                var found = new List<string>();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                    {
                        if (Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        {
                            found.Add(f);
                        }
                        if (found.Count >= 500) break;
                    }
                }
                catch { }
                return found;
            }, ct);

            foreach (var p in paths)
            {
                ct.ThrowIfCancellationRequested();
                Bitmap? thumb = null;
                try
                {
                    await using var fs = File.OpenRead(p);
                    thumb = Bitmap.DecodeToWidth(fs, 160);
                }
                catch { /* unreadable image — skip thumbnail */ }
                var info = new FileInfo(p);
                await Dispatcher.UIThread.InvokeAsync(() => Items.Add(new GalleryItem(
                    Path: p,
                    Name: info.Name,
                    SizeBytes: info.Length,
                    Modified: info.LastWriteTimeUtc,
                    Thumbnail: thumb)));
            }
            StatusLine = $"{Items.Count:N0} image{(Items.Count == 1 ? "" : "s")} loaded.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public sealed record GalleryItem(string Path, string Name, long SizeBytes, DateTimeOffset Modified, Bitmap? Thumbnail);
