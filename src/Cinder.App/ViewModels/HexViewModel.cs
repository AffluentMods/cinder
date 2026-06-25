using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using Cinder.Core.Hashing;
using Cinder.Core.Signatures;
using Cinder.Hex;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

public sealed partial class HexViewModel : ViewModelBase, IDisposable
{
    private readonly SignatureScanner _scanner = new();
    private readonly IHashService _hash = new HashService();

    [ObservableProperty]
    private IHexBuffer? _buffer;

    [ObservableProperty]
    private long _scrollOffset;

    [ObservableProperty]
    private long _caretOffset;

    [ObservableProperty]
    private int _bytesPerRow = 16;

    [ObservableProperty]
    private string? _detectedFormat;

    [ObservableProperty]
    private string? _detectedFormatBadge;

    [ObservableProperty]
    private string? _quickHashSha256;

    [ObservableProperty]
    private string? _quickHashSha256Full;

    public ObservableCollection<HexSearchHit> SearchResults { get; } = new();
    public ObservableCollection<InspectorRow> InspectorRows { get; } = new();
    public ObservableCollection<Bookmark> Bookmarks { get; } = new();

    [ObservableProperty]
    private long _selectionStart = -1;

    [ObservableProperty]
    private long _selectionEnd = -1;

    public bool HasSelection => SelectionStart >= 0 && SelectionEnd >= SelectionStart;
    public long SelectionLength => HasSelection ? SelectionEnd - SelectionStart + 1 : 0;
    public string SelectionSummary => HasSelection
        ? $"selected 0x{SelectionStart:X}–0x{SelectionEnd:X} · {SelectionLength:N0} byte{(SelectionLength == 1 ? "" : "s")}"
        : "";

    private readonly Stack<long> _navBack = new();
    private readonly Stack<long> _navForward = new();
    public bool CanNavigateBack => _navBack.Count > 0;
    public bool CanNavigateForward => _navForward.Count > 0;

    [ObservableProperty]
    private HexSearchHit? _selectedSearchResult;

    // ============== Caret display fields (split for muted/primary styling) ==============

    public string CaretOffsetHex => Buffer is null ? "—" : $"0x{CaretOffset:X}";
    public string CaretOffsetDec => Buffer is null ? "" : $"{CaretOffset:N0}";

    public string CaretByteHex
    {
        get
        {
            var b = ReadCaretByte();
            return b is null ? "—" : $"0x{b.Value:X2}";
        }
    }

    public string CaretByteDec
    {
        get
        {
            var b = ReadCaretByte();
            return b is null ? "" : b.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public string CaretByteAscii
    {
        get
        {
            var b = ReadCaretByte();
            if (b is null)
            {
                return "";
            }
            var c = b is >= 0x20 and < 0x7F ? ((char)b.Value).ToString() : ".";
            return $"'{c}'";
        }
    }

    public string CaretByteBinary
    {
        get
        {
            var b = ReadCaretByte();
            return b is null ? "" : Convert.ToString(b.Value, 2).PadLeft(8, '0');
        }
    }

    private byte? ReadCaretByte()
    {
        if (Buffer is null || CaretOffset >= Buffer.Length)
        {
            return null;
        }
        Span<byte> one = stackalloc byte[1];
        return Buffer.Read(CaretOffset, one) == 0 ? null : (byte?)one[0];
    }

    partial void OnCaretOffsetChanged(long value)
    {
        OnPropertyChanged(nameof(CaretOffsetHex));
        OnPropertyChanged(nameof(CaretOffsetDec));
        OnPropertyChanged(nameof(CaretByteHex));
        OnPropertyChanged(nameof(CaretByteDec));
        OnPropertyChanged(nameof(CaretByteAscii));
        OnPropertyChanged(nameof(CaretByteBinary));
        RefreshInspector();
    }

    partial void OnBufferChanged(IHexBuffer? value)
    {
        OnPropertyChanged(nameof(CaretOffsetHex));
        OnPropertyChanged(nameof(CaretOffsetDec));
        OnPropertyChanged(nameof(CaretByteHex));
        OnPropertyChanged(nameof(CaretByteDec));
        OnPropertyChanged(nameof(CaretByteAscii));
        OnPropertyChanged(nameof(CaretByteBinary));
        SearchResults.Clear();
        Bookmarks.Clear();
        _navBack.Clear();
        _navForward.Clear();
        SelectionStart = -1;
        SelectionEnd = -1;
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
        RefreshInspector();
    }

    partial void OnSelectionStartChanged(long value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionLength));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    partial void OnSelectionEndChanged(long value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionLength));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void RefreshInspector()
    {
        InspectorRows.Clear();
        if (Buffer is null || CaretOffset >= Buffer.Length)
        {
            return;
        }
        Span<byte> bytes = stackalloc byte[16];
        var read = Buffer.Read(CaretOffset, bytes);
        var rows = Inspector.Decode(bytes[..read]);
        foreach (var r in rows)
        {
            InspectorRows.Add(r);
        }
    }

    partial void OnSelectedSearchResultChanged(HexSearchHit? value)
    {
        if (value is null || Buffer is null)
        {
            return;
        }
        JumpTo(value.Offset);
    }

    public HexViewModel() { }

    public void OpenFile(string path)
    {
        Buffer?.Dispose();
        Buffer = new MmapHexBuffer(path);
        ScrollOffset = 0;
        CaretOffset = 0;
        AnalyzeHeader(path);
        _ = QuickHashAsync();
    }

    private void AnalyzeHeader(string path)
    {
        if (Buffer is null)
        {
            return;
        }
        var header = new byte[(int)Math.Min(Buffer.Length, 0x40000)];
        var read = Buffer.Read(0, header);
        var hits = _scanner.Scan(header.AsSpan(0, read));
        if (hits.Count == 0)
        {
            // No fixed-pattern match — try the entropy heuristic. TrueCrypt / VeraCrypt
            // containers ship no magic by design; flag them by shape (size + entropy).
            var heur = EncryptedContainerHeuristic.Inspect(
                header.AsSpan(0, read),
                Buffer.Length,
                hits);
            if (heur.Looks)
            {
                DetectedFormat = $"Probable encrypted container (entropy {heur.Entropy:F2})";
                DetectedFormatBadge = "⚠ High-entropy data — likely TrueCrypt / VeraCrypt container or already-encrypted blob";
                DetectedRouting = "→ Cannot parse without passphrase";
                return;
            }
            DetectedFormat = "Unknown";
            DetectedFormatBadge = null;
            DetectedRouting = null;
            return;
        }
        var best = hits[0];
        var matchedBytes = FormatMatchedBytes(header, best.Signature, read);
        DetectedFormat = $"{best.Signature.Label} ({matchedBytes} at 0x{best.Offset:X})";
        DetectedFormatBadge = _scanner.IsExtensionMismatch(path, header.AsSpan(0, read), out _)
            ? $"⚠ Extension mismatch — content looks like .{best.Signature.Extension}"
            : null;
        DetectedRouting = RouteForExtension(best.Signature.Extension);
    }

    [ObservableProperty]
    private string? _detectedRouting;

    /// <summary>
    /// Maps a magic-detected extension to the parser pane that *would* handle it once Cinder's
    /// sidecar runtime is bootstrapped (Phase 3+). Phase 1 just surfaces the suggestion in the
    /// header so the user knows the file would be parsed.
    /// </summary>
    private static string? RouteForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        "hive" => "→ Registry parser (Phase 4)",
        "evtx" => "→ Event Log viewer (Phase 4)",
        "evt" => "→ Event Log viewer (Phase 4)",
        "lnk" => "→ LNK parser (Phase 4)",
        "pf" => "→ Prefetch viewer (Phase 4)",
        "pst" or "ost" => "→ Email parser (Phase 4)",
        "pcap" or "pcapng" => "→ PCAP analyzer (Phase 10)",
        "e01" or "ex01" or "aff4" or "vmdk" or "vhd" or "vhdx" or "qcow" or "vdi" => "→ Disk imager (Phase 2)",
        "ntfs" or "ext" or "fat" or "apfs" or "hfsplus" or "btrfs" or "xfs" or "iso" => "→ Filesystem browser (Phase 3)",
        "sqlite" => "→ SQLite browser",
        "elf" or "exe" or "macho" or "class" or "wasm" => "→ Executable inspector",
        "jpg" or "png" or "gif" or "bmp" or "webp" or "heic" or "avif" or "tiff" => "→ Image gallery (Phase 4)",
        "mp4" or "mkv" or "avi" => "→ Video preview (Phase 4)",
        "pdf" => "→ Document preview (Phase 4)",
        "docx" or "xlsx" or "pptx" or "doc" or "xls" or "ppt" => "→ Document preview (Phase 4)",
        _ => null,
    };

    private static string FormatMatchedBytes(byte[] header, MagicSignature sig, int validRead)
    {
        var sb = new StringBuilder("0x");
        var max = Math.Min(sig.Pattern.Length, 6);
        for (int i = 0; i < max; i++)
        {
            var idx = (int)(sig.Offset + i);
            if (idx < 0 || idx >= validRead)
            {
                break;
            }
            sb.Append(header[idx].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private async Task QuickHashAsync()
    {
        if (Buffer is null)
        {
            return;
        }
        try
        {
            // Hash up to 4 MiB for the "quick" badge — full hash is the dedicated dialog's job.
            var sample = new byte[(int)Math.Min(Buffer.Length, 4L << 20)];
            var read = Buffer.Read(0, sample);
            using var ms = new MemoryStream(sample, 0, read, writable: false);
            var result = await _hash.ComputeAsync(ms, [HashAlgorithmKind.Sha256]);
            QuickHashSha256Full = result.Sha256;
            QuickHashSha256 = result.Sha256?[..16] + "…";
        }
        catch
        {
            QuickHashSha256 = null;
            QuickHashSha256Full = null;
        }
    }

    [RelayCommand]
    private async Task CopyQuickHashAsync()
    {
        if (string.IsNullOrEmpty(QuickHashSha256Full))
        {
            return;
        }
        try
        {
            var clipboard = Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow?.Clipboard
                : null;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(QuickHashSha256Full);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    [RelayCommand]
    private void AddBookmark()
    {
        if (Buffer is null)
        {
            return;
        }
        var existing = Bookmarks.FirstOrDefault(b => b.Offset == CaretOffset);
        if (existing is not null)
        {
            Bookmarks.Remove(existing);
            return;
        }
        var label = $"Bookmark {Bookmarks.Count + 1} · 0x{CaretOffset:X}";
        Bookmarks.Add(new Bookmark(CaretOffset, label));
    }

    [RelayCommand]
    private void GotoBookmark(Bookmark? bookmark)
    {
        if (bookmark is null || Buffer is null)
        {
            return;
        }
        JumpTo(bookmark.Offset);
    }

    [RelayCommand(CanExecute = nameof(CanNavigateBack))]
    private void NavigateBack()
    {
        if (!_navBack.TryPop(out var prev))
        {
            return;
        }
        _navForward.Push(CaretOffset);
        ApplyJump(prev);
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
    }

    [RelayCommand(CanExecute = nameof(CanNavigateForward))]
    private void NavigateForward()
    {
        if (!_navForward.TryPop(out var next))
        {
            return;
        }
        _navBack.Push(CaretOffset);
        ApplyJump(next);
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
    }

    /// <summary>Jump the caret to a specific offset, recording the prior caret on the back-stack.</summary>
    public void JumpTo(long offset)
    {
        if (Buffer is null || offset < 0 || offset >= Buffer.Length)
        {
            return;
        }
        if (CaretOffset != offset)
        {
            _navBack.Push(CaretOffset);
            _navForward.Clear();
            OnPropertyChanged(nameof(CanNavigateBack));
            OnPropertyChanged(nameof(CanNavigateForward));
        }
        ApplyJump(offset);
    }

    private void ApplyJump(long offset)
    {
        CaretOffset = offset;
        ScrollOffset = (offset / BytesPerRow) * BytesPerRow;
    }

    [RelayCommand]
    private async Task CopySelectionAsHexAsync()
    {
        if (!HasSelection || Buffer is null)
        {
            return;
        }
        var len = (int)Math.Min(SelectionLength, 1 << 20); // cap at 1 MiB to avoid clipboard abuse
        var bytes = new byte[len];
        Buffer.Read(SelectionStart, bytes);
        var sb = new StringBuilder(len * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        await CopyToClipboardAsync(sb.ToString());
    }

    private static async Task CopyToClipboardAsync(string text)
    {
        try
        {
            var clipboard = Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow?.Clipboard
                : null;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch { /* best effort */ }
    }

    [RelayCommand]
    private void GotoOffset(string raw)
    {
        if (Buffer is null || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }
        long parsed;
        var s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
            {
                return;
            }
        }
        else if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
        {
            return;
        }
        JumpTo(parsed);
    }

    /// <summary>Run a search across the open buffer and surface the first ~1000 hits.</summary>
    public async Task SearchAsync(HexSearchOptions options, CancellationToken ct = default)
    {
        if (Buffer is null)
        {
            return;
        }
        SearchResults.Clear();
        var hits = await Task.Run(() =>
        {
            var bag = new List<HexSearchHit>();
            foreach (var hit in HexSearch.Search(Buffer, options, ct))
            {
                bag.Add(hit);
                if (bag.Count >= 1000)
                {
                    break;
                }
            }
            return bag;
        }, ct);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var h in hits)
            {
                SearchResults.Add(h);
            }
            if (hits.Count > 0)
            {
                SelectedSearchResult = hits[0];
            }
        });
    }

    public void Dispose()
    {
        Buffer?.Dispose();
        Buffer = null;
    }
}
