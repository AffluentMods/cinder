using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
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
    public override string Subtitle => "Browse the pictures in a folder as thumbnails and read each one's camera details and GPS position.";
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

    /// <summary>Thumbnails shown for one folder. Reaching it is stated in the status line.</summary>
    internal const int MaxImages = 500;

    /// <summary>Identifies the newest preview request, so a slow earlier image cannot land on a later one.</summary>
    private int _previewSerial;

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
        StatusLine = "Looking for images…";
        Items.Clear();
        SelectedItem = null;
        try
        {
            var (paths, more) = await Task.Run(() => FindImages(folder, MaxImages, ct), ct);

            // Decoding is the slow part and runs on the thread pool, a few images at a time, so
            // the window stays responsive and thumbnails appear as they are ready.
            const int Batch = 16;
            for (var i = 0; i < paths.Count; i += Batch)
            {
                ct.ThrowIfCancellationRequested();
                var slice = paths.GetRange(i, Math.Min(Batch, paths.Count - i));
                var items = await Task.Run(() => slice.ConvertAll(MakeItem), ct);
                foreach (var item in items)
                {
                    Items.Add(item);
                }
                StatusLine = $"Loading thumbnails… {Items.Count:N0} of {paths.Count:N0}";
            }
            StatusLine = DescribeLoad(Items.Count, more);
        }
        catch (OperationCanceledException)
        {
            StatusLine = $"Stopped. {Items.Count:N0} image{(Items.Count == 1 ? "" : "s")} loaded before stopping.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Could not read {folder}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// The first <paramref name="max"/> image files under <paramref name="folder"/>, and whether
    /// there are more than that. Subfolders that cannot be opened are skipped, not fatal.
    /// </summary>
    internal static (List<string> Paths, bool More) FindImages(string folder, int max, CancellationToken ct)
    {
        var found = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var f in System.IO.Directory.EnumerateFiles(folder, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            if (found.Count >= max)
            {
                return (found, true);
            }
            found.Add(f);
        }
        return (found, false);
    }

    /// <summary>The status line after a load. When the cap was reached it says so, and what to do about it.</summary>
    internal static string DescribeLoad(int shown, bool more) => more
        ? $"Showing the first {shown:N0} images. This folder holds more than that; pick a subfolder to see the others."
        : shown == 0
            ? "No images were found in this folder or its subfolders."
            : $"{shown:N0} image{(shown == 1 ? "" : "s")}.";

    private static GalleryItem MakeItem(string path)
    {
        Bitmap? thumb = null;
        try
        {
            using var fs = File.OpenRead(path);
            thumb = Bitmap.DecodeToWidth(fs, 160);
        }
        catch
        {
            // Not an image Cinder can draw (HEIC, AVIF, damaged file): listed without a thumbnail.
        }
        var info = new FileInfo(path);
        return new GalleryItem(path, info.Name, info.Length, info.LastWriteTimeUtc, thumb);
    }

    private async Task LoadPreviewAsync(GalleryItem? item)
    {
        var serial = ++_previewSerial;
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
            if (serial != _previewSerial)
            {
                // Another image was selected while this one was loading. Its details must not
                // appear under the other image's name.
                bmp?.Dispose();
                return;
            }
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
            if (serial == _previewSerial)
            {
                ExifRows.Add(new ExifRow("Preview", $"Could not load this image: {ex.Message}"));
            }
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
            exif.Add(new ExifRow("EXIF", $"Could not read the image's metadata: {ex.Message}"));
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
