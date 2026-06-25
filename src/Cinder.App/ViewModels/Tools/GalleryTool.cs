using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Exif.Makernotes;

namespace Cinder.App.ViewModels.Tools;

public sealed partial class GalleryTool : ToolViewModel
{
    public override string Id => "gallery";
    public override string Title => "Image gallery";
    public override string Icon => "🖼";
    public override string Subtitle => "Folder of images → thumbnails · click to preview · EXIF · GPS.";
    public override string Phase => "1";
    public override string Kind => "gallery";

    public ObservableCollection<GalleryItem> Items { get; } = new();
    public ObservableCollection<ExifRow> ExifRows { get; } = new();

    [ObservableProperty] private string? _folderPath;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _statusLine;

    [ObservableProperty] private GalleryItem? _selectedItem;
    [ObservableProperty] private Bitmap? _previewBitmap;
    [ObservableProperty] private string? _previewPath;
    [ObservableProperty] private string? _previewSize;
    [ObservableProperty] private string? _previewModified;
    [ObservableProperty] private string? _previewDimensions;
    [ObservableProperty] private string? _previewGps;

    private static readonly string[] Extensions =
    [
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".avif", ".tiff", ".tif", ".ico",
    ];

    partial void OnSelectedItemChanged(GalleryItem? value)
    {
        _ = LoadPreviewAsync(value);
    }

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

    [RelayCommand]
    private void ClosePreview()
    {
        SelectedItem = null;
    }

    private async Task LoadAsync(string folder, CancellationToken ct)
    {
        IsLoading = true;
        StatusLine = "Scanning…";
        Items.Clear();
        SelectedItem = null;
        try
        {
            var paths = await Task.Run(() =>
            {
                var found = new List<string>();
                try
                {
                    foreach (var f in System.IO.Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
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

    private async Task LoadPreviewAsync(GalleryItem? item)
    {
        PreviewBitmap?.Dispose();
        PreviewBitmap = null;
        ExifRows.Clear();

        if (item is null)
        {
            PreviewPath = null;
            PreviewSize = null;
            PreviewModified = null;
            PreviewDimensions = null;
            PreviewGps = null;
            return;
        }

        PreviewPath = item.Path;
        PreviewSize = $"{item.SizeBytes:N0} bytes";
        PreviewModified = item.Modified.ToString("u", CultureInfo.InvariantCulture);

        try
        {
            var (bmp, exif, dims, gps) = await Task.Run(() => LoadFullSize(item.Path));
            PreviewBitmap = bmp;
            PreviewDimensions = dims;
            PreviewGps = gps;
            foreach (var row in exif)
            {
                ExifRows.Add(row);
            }
        }
        catch (Exception ex)
        {
            ExifRows.Add(new ExifRow("Preview", $"Failed to load: {ex.Message}"));
        }
    }

    private static (Bitmap? bmp, List<ExifRow> exif, string dims, string? gps) LoadFullSize(string path)
    {
        Bitmap? bmp = null;
        try
        {
            using var fs = File.OpenRead(path);
            bmp = new Bitmap(fs);
        }
        catch { /* keep bmp null — UI shows "preview unavailable" */ }

        var exif = new List<ExifRow>();
        string dims = bmp is null ? "" : $"{bmp.PixelSize.Width} × {bmp.PixelSize.Height}";
        string? gps = null;

        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            foreach (var dir in directories)
            {
                // Pull the obviously-useful fields from the common directories.
                if (dir is ExifSubIfdDirectory sub)
                {
                    AddIfPresent(sub, ExifDirectoryBase.TagDateTimeOriginal, "EXIF · DateTimeOriginal", exif);
                    AddIfPresent(sub, ExifDirectoryBase.TagDateTimeDigitized, "EXIF · DateTimeDigitized", exif);
                    AddIfPresent(sub, ExifDirectoryBase.TagExposureTime, "EXIF · ExposureTime", exif);
                    AddIfPresent(sub, ExifDirectoryBase.TagFNumber, "EXIF · F-number", exif);
                    AddIfPresent(sub, ExifDirectoryBase.TagIsoEquivalent, "EXIF · ISO", exif);
                    AddIfPresent(sub, ExifDirectoryBase.TagFocalLength, "EXIF · FocalLength", exif);
                    AddIfPresent(sub, ExifDirectoryBase.TagLensModel, "EXIF · LensModel", exif);
                }
                if (dir is ExifIfd0Directory ifd0)
                {
                    AddIfPresent(ifd0, ExifDirectoryBase.TagMake, "Camera · Make", exif);
                    AddIfPresent(ifd0, ExifDirectoryBase.TagModel, "Camera · Model", exif);
                    AddIfPresent(ifd0, ExifDirectoryBase.TagSoftware, "Camera · Software", exif);
                    AddIfPresent(ifd0, ExifDirectoryBase.TagOrientation, "Camera · Orientation", exif);
                }
                if (dir is GpsDirectory g)
                {
                    var loc = g.GetGeoLocation();
                    if (loc is not null)
                    {
                        var lat = loc.Value.Latitude;
                        var lon = loc.Value.Longitude;
                        gps = string.Create(CultureInfo.InvariantCulture, $"{lat:F6}, {lon:F6}");
                        exif.Add(new ExifRow("GPS · Latitude", lat.ToString("F6", CultureInfo.InvariantCulture)));
                        exif.Add(new ExifRow("GPS · Longitude", lon.ToString("F6", CultureInfo.InvariantCulture)));
                    }
                    AddIfPresent(g, GpsDirectory.TagAltitude, "GPS · Altitude", exif);
                    AddIfPresent(g, GpsDirectory.TagSpeed, "GPS · Speed", exif);
                    AddIfPresent(g, GpsDirectory.TagDateStamp, "GPS · DateStamp", exif);
                }
            }
        }
        catch (Exception ex)
        {
            exif.Add(new ExifRow("EXIF", $"Read failed: {ex.Message}"));
        }

        return (bmp, exif, dims, gps);
    }

    private static void AddIfPresent(MetadataExtractor.Directory dir, int tag, string label, List<ExifRow> rows)
    {
        var s = dir.GetDescription(tag);
        if (!string.IsNullOrWhiteSpace(s))
        {
            rows.Add(new ExifRow(label, s));
        }
    }
}

public sealed record GalleryItem(string Path, string Name, long SizeBytes, DateTimeOffset Modified, Bitmap? Thumbnail);

public sealed record ExifRow(string Label, string Value);
