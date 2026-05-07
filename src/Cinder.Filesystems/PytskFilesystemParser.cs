using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Filesystems;

/// <summary>
/// <see cref="IFilesystemParser"/> backed by the Python <c>parsers/filesystem/fs_worker.py</c>
/// sidecar, which wraps pytsk3 (Sleuth Kit) for NTFS / FAT / ext / APFS / HFS+ / UDF / ISO9660
/// / Btrfs / XFS, plus optional native parsers (libfsapfs, libfsbtrfs) when installed.
///
/// pytsk3 must be installed in the bundled venv (<c>pip install pytsk3 libewf-python</c>). The
/// sidecar surfaces a clean error if it isn't.
/// </summary>
public sealed class PytskFilesystemParser : IFilesystemParser
{
    private readonly Func<ProcessStartInfo> _sidecarFactory;
    private readonly ILogger<PytskFilesystemParser> _logger;

    public PytskFilesystemParser(Func<ProcessStartInfo> sidecarFactory, ILogger<PytskFilesystemParser>? logger = null)
    {
        _sidecarFactory = sidecarFactory ?? throw new ArgumentNullException(nameof(sidecarFactory));
        _logger = logger ?? NullLogger<PytskFilesystemParser>.Instance;
    }

    public static ProcessStartInfo DefaultSidecar(string parsersDir) => new()
    {
        FileName = OperatingSystem.IsWindows() ? "python.exe" : "python3",
        ArgumentList = { "-m", "filesystem.fs_worker" },
        WorkingDirectory = parsersDir,
    };

    public async Task<FilesystemInfo> IdentifyAsync(string imagePath, long offsetBytes = 0, CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_sidecarFactory(), _logger);
        var resp = await sc.InvokeAsync("identify",
            new JsonObject { ["image_path"] = imagePath, ["offset"] = offsetBytes }, ct).ConfigureAwait(false);
        if (resp is not JsonObject o)
        {
            throw new SidecarException("identify returned no result");
        }
        return new FilesystemInfo(
            Kind: Enum.TryParse<FilesystemKind>(o["kind"]?.GetValue<string>() ?? "Unknown", true, out var k) ? k : FilesystemKind.Unknown,
            Label: o["label"]?.GetValue<string?>(),
            VolumeSize: o["volume_size"]?.GetValue<long?>(),
            ClusterSize: o["cluster_size"]?.GetValue<int?>(),
            Extras: ExtractExtras(o["extras"]));
    }

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(string imagePath, long offsetBytes = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_sidecarFactory(), _logger);
        long cursor = 0;
        while (true)
        {
            var resp = await sc.InvokeAsync("enumerate_page",
                new JsonObject { ["image_path"] = imagePath, ["offset"] = offsetBytes, ["cursor"] = cursor, ["limit"] = 500 },
                ct).ConfigureAwait(false);
            if (resp is not JsonObject o || o["entries"] is not JsonArray rows)
            {
                yield break;
            }
            foreach (var row in rows.OfType<JsonObject>())
            {
                yield return new FileEntry(
                    Inode: row["inode"]?.GetValue<long>() ?? 0,
                    Path: row["path"]?.GetValue<string>() ?? "",
                    Name: row["name"]?.GetValue<string>() ?? "",
                    Size: row["size"]?.GetValue<long>() ?? 0,
                    IsDirectory: row["is_dir"]?.GetValue<bool>() ?? false,
                    IsDeleted: row["is_deleted"]?.GetValue<bool>() ?? false,
                    CreatedUtc: ParseTime(row["btime"]),
                    ModifiedUtc: ParseTime(row["mtime"]),
                    AccessedUtc: ParseTime(row["atime"]),
                    MetadataChangedUtc: ParseTime(row["ctime"]),
                    Owner: row["owner"]?.GetValue<string?>(),
                    Group: row["group"]?.GetValue<string?>(),
                    UnixMode: row["mode"]?.GetValue<int?>(),
                    Extras: ExtractExtras(row["extras"]));
            }
            if (rows.Count < 500)
            {
                yield break;
            }
            cursor = (o["next_cursor"]?.GetValue<long?>()) ?? cursor + rows.Count;
        }
    }

    public async Task<byte[]> ReadFileAsync(string imagePath, long inode, long offsetBytes = 0, CancellationToken ct = default)
    {
        await using var sc = new SidecarClient(_sidecarFactory(), _logger);
        var resp = await sc.InvokeAsync("read_file",
            new JsonObject { ["image_path"] = imagePath, ["offset"] = offsetBytes, ["inode"] = inode }, ct).ConfigureAwait(false);
        var b64 = (resp as JsonObject)?["data_b64"]?.GetValue<string>();
        return string.IsNullOrEmpty(b64) ? [] : Convert.FromBase64String(b64);
    }

    private static DateTimeOffset? ParseTime(JsonNode? n)
    {
        var s = n?.GetValue<string?>();
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            return dto;
        }
        return null;
    }

    private static IReadOnlyDictionary<string, string>? ExtractExtras(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return null;
        }
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in o)
        {
            dict[kv.Key] = kv.Value?.ToString() ?? "";
        }
        return dict;
    }
}
