using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Cinder.Core.Diagnostics;
using Cinder.Sidecar;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cinder.Filesystems;

/// <summary>One page of a filesystem listing, as the sidecar returned it.</summary>
/// <param name="Entries">The entries on this page, in depth-first order.</param>
/// <param name="NextCursor">Cursor of the page after this one.</param>
/// <param name="Done">True when this is the last page.</param>
/// <param name="Errors">
/// Entries and directories the walk could not read <em>so far</em> (cumulative across pages).
/// Anything above zero means the listing is missing part of the volume.
/// </param>
public sealed record FileListingPage(IReadOnlyList<FileEntry> Entries, long NextCursor, bool Done, int Errors);

/// <summary>Bytes read from a file in an image.</summary>
/// <param name="Data">The bytes, from the start of the file.</param>
/// <param name="FileSize">The size the filesystem declares for the file.</param>
/// <param name="Truncated">
/// True when <paramref name="Data"/> stops at the caller's cap and the file continues. False
/// when the whole file was read — including when it turned out shorter than
/// <paramref name="FileSize"/> (a hostile image can declare any size).
/// </param>
public sealed record FileReadResult(byte[] Data, long FileSize, bool Truncated);

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
    /// <summary>Entries requested per listing page.</summary>
    public const int ListingPageSize = 500;

    /// <summary>
    /// Bytes requested per <c>read_file</c> call. The sidecar caps one reply at 8 MiB (it is a
    /// single base64 JSON line); asking for less keeps each line comfortably small.
    /// </summary>
    public const int ReadChunkBytes = 4 * 1024 * 1024;

    /// <summary>Default for <see cref="MaxFileBytes"/>.</summary>
    public const long DefaultMaxFileBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Cap applied by the <see cref="IFilesystemParser.ReadFileAsync"/> overload that takes
    /// none. That overload returns a <c>byte[]</c> and so has to hold the whole file in memory;
    /// a file larger than this is refused there (not truncated) and has to be read through
    /// <see cref="ReadFileAsync(string, long, long, long, CancellationToken)"/> or
    /// <see cref="CopyFileToAsync"/> with a cap the caller chose.
    /// </summary>
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    private readonly Func<ISidecarChannel> _open;
    private readonly ILogger<PytskFilesystemParser> _logger;

    public PytskFilesystemParser(Func<ProcessStartInfo> sidecarFactory, ILogger<PytskFilesystemParser>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sidecarFactory);
        _logger = logger ?? NullLogger<PytskFilesystemParser>.Instance;
        _open = () => new SidecarClient(sidecarFactory(), _logger);
    }

    /// <summary>Uses an already-built channel factory (tests, or a host that pools sidecars).</summary>
    public PytskFilesystemParser(Func<ISidecarChannel> channelFactory, ILogger<PytskFilesystemParser>? logger = null)
    {
        _open = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _logger = logger ?? NullLogger<PytskFilesystemParser>.Instance;
    }

    public static ProcessStartInfo DefaultSidecar(string parsersDir) => new()
    {
        FileName = ExecutableResolver.ResolveRequired(
            OperatingSystem.IsWindows() ? "python.exe" : "python3",
            "Install Python 3.12+ and make sure it is on PATH."),
        ArgumentList = { "-m", "filesystem.fs_worker" },
        WorkingDirectory = parsersDir,
    };

    public async Task<FilesystemInfo> IdentifyAsync(string imagePath, long offsetBytes = 0, CancellationToken ct = default)
    {
        await using var sc = _open();
        var resp = await sc.InvokeAsync("identify",
            new JsonObject { ["image_path"] = imagePath, ["offset"] = offsetBytes }, null, null, ct).ConfigureAwait(false);
        if (resp is not JsonObject o)
        {
            throw new SidecarException("identify returned no result");
        }
        return new FilesystemInfo(
            Kind: Enum.TryParse<FilesystemKind>(SidecarJson.String(o["kind"]) ?? "Unknown", true, out var k) ? k : FilesystemKind.Unknown,
            Label: SidecarJson.String(o["label"]),
            VolumeSize: SidecarJson.Int64(o["volume_size"]),
            ClusterSize: SidecarJson.Int32(o["cluster_size"]),
            Extras: ExtractExtras(o["extras"]));
    }

    /// <summary>
    /// The listing, page by page, with each page's cursor, end marker and running error count.
    /// Use this rather than <see cref="EnumerateAsync"/> wherever the listing is shown as "the
    /// contents of the volume": only the pages say whether part of it could not be read.
    /// </summary>
    public async IAsyncEnumerable<FileListingPage> EnumeratePagesAsync(string imagePath, long offsetBytes = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // One channel for the whole listing: the worker keeps its walk between pages, so page
        // N+1 continues where page N stopped instead of re-walking from the root.
        await using var sc = _open();
        long cursor = 0;
        while (true)
        {
            var resp = await sc.InvokeAsync("enumerate_page",
                new JsonObject { ["image_path"] = imagePath, ["offset"] = offsetBytes, ["cursor"] = cursor, ["limit"] = ListingPageSize },
                null, null, ct).ConfigureAwait(false);
            if (resp is not JsonObject o || o["entries"] is not JsonArray rows)
            {
                throw new SidecarException("enumerate_page returned no entries array; the listing is incomplete.");
            }

            var entries = new List<FileEntry>(rows.Count);
            foreach (var row in rows.OfType<JsonObject>())
            {
                entries.Add(ToEntry(row));
            }

            // The worker says when it is done. A worker that does not (an older one) is done
            // when a page comes back short.
            var done = SidecarJson.Boolean(o["done"]) ?? rows.Count < ListingPageSize;
            var next = SidecarJson.Int64(o["next_cursor"]) ?? cursor + rows.Count;
            if (!done && next <= cursor)
            {
                // A cursor that does not advance would repeat this page forever.
                throw new SidecarException($"enumerate_page did not advance past cursor {cursor}; the listing is incomplete.");
            }

            yield return new FileListingPage(entries, next, done, SidecarJson.Int32(o["errors"]) ?? 0);
            if (done)
            {
                yield break;
            }
            cursor = next;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Entries only. If the walk could not read part of the volume that is logged as a warning;
    /// callers that must show it use <see cref="EnumeratePagesAsync"/>.
    /// </remarks>
    public async IAsyncEnumerable<FileEntry> EnumerateAsync(string imagePath, long offsetBytes = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var errors = 0;
        await foreach (var page in EnumeratePagesAsync(imagePath, offsetBytes, ct).ConfigureAwait(false))
        {
            errors = page.Errors;
            foreach (var entry in page.Entries)
            {
                yield return entry;
            }
        }
        if (errors > 0)
        {
            _logger.LogWarning(
                "Filesystem listing of {Image} is incomplete: {Errors} entr(ies) or director(ies) could not be read.",
                imagePath, errors);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the whole file, in ranges. A file larger than <see cref="MaxFileBytes"/>
    /// fails with an <see cref="IOException"/>: this overload never returns part of a file as
    /// if it were all of it.
    /// </remarks>
    public async Task<byte[]> ReadFileAsync(string imagePath, long inode, long offsetBytes = 0, CancellationToken ct = default)
    {
        var result = await ReadFileAsync(imagePath, inode, offsetBytes, MaxFileBytes, ct).ConfigureAwait(false);
        if (result.Truncated)
        {
            throw new IOException(
                $"File (inode {inode}) is {result.FileSize} bytes, more than the {MaxFileBytes}-byte limit for reading a file " +
                "into memory. Read it with an explicit cap or copy it to a stream.");
        }
        return result.Data;
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> of a file into memory and says whether that was
    /// all of it.
    /// </summary>
    /// <param name="imagePath">The image.</param>
    /// <param name="inode">Inode / MFT entry of the file.</param>
    /// <param name="offsetBytes">Offset of the volume inside the image.</param>
    /// <param name="maxBytes">The most to read; must be between 0 and <see cref="Array.MaxLength"/>.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<FileReadResult> ReadFileAsync(string imagePath, long inode, long offsetBytes, long maxBytes, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, Array.MaxLength);
        using var buffer = new MemoryStream();
        var (fileSize, truncated) = await CopyFileToAsync(imagePath, inode, buffer, offsetBytes, maxBytes, ct).ConfigureAwait(false);
        return new FileReadResult(buffer.ToArray(), fileSize, truncated);
    }

    /// <summary>
    /// Copies at most <paramref name="maxBytes"/> of a file to <paramref name="destination"/>,
    /// one bounded range per sidecar call, so a file of any size can be extracted without
    /// holding it in memory.
    /// </summary>
    /// <returns>The size the filesystem declares for the file, and whether the copy stopped at
    /// the cap with more of the file left.</returns>
    public async Task<(long FileSize, bool Truncated)> CopyFileToAsync(string imagePath, long inode, Stream destination,
        long offsetBytes = 0, long maxBytes = long.MaxValue, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        await using var sc = _open();
        long position = 0;
        long fileSize = 0;
        while (true)
        {
            // Ask for one byte more than the cap allows on the last range: that byte is how
            // "the file ends exactly at the cap" is told apart from "there is more".
            var room = maxBytes - position;
            var want = room >= ReadChunkBytes ? ReadChunkBytes : (int)room + 1;
            var resp = await sc.InvokeAsync("read_file",
                new JsonObject
                {
                    ["image_path"] = imagePath,
                    ["offset"] = offsetBytes,
                    ["inode"] = inode,
                    ["file_offset"] = position,
                    ["length"] = want,
                },
                null, null, ct).ConfigureAwait(false);
            if (resp is not JsonObject o)
            {
                throw new SidecarException("read_file returned no result.");
            }

            byte[] chunk;
            try
            {
                chunk = Convert.FromBase64String(SidecarJson.String(o["data_b64"]) ?? "");
            }
            catch (FormatException ex)
            {
                throw new SidecarException("read_file returned data that is not valid base64.", ex);
            }
            if (chunk.Length > want)
            {
                throw new SidecarException($"read_file returned {chunk.Length} bytes for a {want}-byte request.");
            }
            fileSize = SidecarJson.Int64(o["size"]) ?? fileSize;

            if (chunk.Length > room)
            {
                // The extra byte arrived: the file goes on past the cap.
                await destination.WriteAsync(chunk.AsMemory(0, (int)room), ct).ConfigureAwait(false);
                return (fileSize, true);
            }
            await destination.WriteAsync(chunk, ct).ConfigureAwait(false);
            position += chunk.Length;

            // "eof" from the worker; for a worker that does not send it, an empty or short
            // range is the end. An empty range always is: there is nothing to advance by.
            var eof = SidecarJson.Boolean(o["eof"]) ?? chunk.Length < want;
            if (eof || chunk.Length == 0)
            {
                return (fileSize, false);
            }
        }
    }

    private static FileEntry ToEntry(JsonObject row) => new(
        Inode: SidecarJson.Int64(row["inode"]) ?? 0,
        Path: SidecarJson.String(row["path"]) ?? "",
        Name: SidecarJson.String(row["name"]) ?? "",
        Size: SidecarJson.Int64(row["size"]) ?? 0,
        IsDirectory: SidecarJson.Boolean(row["is_dir"]) ?? false,
        IsDeleted: SidecarJson.Boolean(row["is_deleted"]) ?? false,
        // The worker converts epoch seconds and always writes +00:00; a value it could not
        // represent (timestomped past year 9999, garbage) arrives as null and stays null.
        CreatedUtc: SidecarJson.Instant(row["btime"]),
        ModifiedUtc: SidecarJson.Instant(row["mtime"]),
        AccessedUtc: SidecarJson.Instant(row["atime"]),
        MetadataChangedUtc: SidecarJson.Instant(row["ctime"]),
        Owner: SidecarJson.String(row["owner"]),
        Group: SidecarJson.String(row["group"]),
        UnixMode: SidecarJson.Int32(row["mode"]),
        Extras: ExtractExtras(row["extras"]));

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
