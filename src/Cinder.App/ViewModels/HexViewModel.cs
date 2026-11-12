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
        RaiseCaretPanel();
        RefreshInspector();
    }

    partial void OnBufferChanged(IHexBuffer? value)
    {
        OnPropertyChanged(nameof(TabTitle));
        _hashCts?.Cancel();
        _findCts?.Cancel();
        HashStatus = null;
        FindStatus = null;
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(SizeTip));
        OnPropertyChanged(nameof(CanStartHash));
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
        RaiseCaretPanel();
        RefreshInspector();
    }

    partial void OnSelectionStartChanged(long value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionLength));
        OnPropertyChanged(nameof(SelectionSummary));
        RaiseSelectionPanel();
    }

    partial void OnSelectionEndChanged(long value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionLength));
        OnPropertyChanged(nameof(SelectionSummary));
        RaiseSelectionPanel();
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
        foreach (var r in Inspector.Decode(bytes[..read], IsLittleEndian, DateTimeOffset.UtcNow))
        {
            InspectorRows.Add(r);
        }
    }

    // ============================ Inspector panel ============================

    [ObservableProperty]
    private bool _isLittleEndian = true;

    partial void OnIsLittleEndianChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBigEndian));
        RefreshInspector();
    }

    public bool IsBigEndian => !IsLittleEndian;

    [RelayCommand]
    private void SetEndian(string? which) => IsLittleEndian = !string.Equals(which, "be", StringComparison.OrdinalIgnoreCase);

    /// <summary>Offset digits: 8 up to 4 GiB, 16 beyond — matches the grid gutter.</summary>
    private string OffsetFormat => Buffer is { Length: > 0xFFFFFFFFL } ? "X16" : "X8";

    public string CaretOffsetPadded => Buffer is null ? "—" : "0x" + CaretOffset.ToString(OffsetFormat, CultureInfo.InvariantCulture);

    public string CaretByteHexShort
    {
        get
        {
            var b = ReadCaretByte();
            return b is null ? "—" : b.Value.ToString("X2", CultureInfo.InvariantCulture);
        }
    }

    public string CaretByteChar
    {
        get
        {
            var b = ReadCaretByte();
            return b switch
            {
                null => "—",
                0 => "NUL",
                9 => "\\t",
                10 => "\\n",
                13 => "\\r",
                32 => "space",
                >= 0x21 and < 0x7F => "'" + (char)b.Value + "'",
                _ => "·",
            };
        }
    }

    public string CaretByteBinarySpaced
    {
        get
        {
            var b = ReadCaretByte();
            if (b is null)
            {
                return "—";
            }
            var s = Convert.ToString(b.Value, 2).PadLeft(8, '0');
            return s[..4] + " " + s[4..];
        }
    }

    /// <summary>Selection, or the single caret byte when nothing is selected.</summary>
    public long EffectiveSelectionStart => HasSelection ? SelectionStart : CaretOffset;
    public long EffectiveSelectionEnd => HasSelection ? SelectionEnd : CaretOffset;

    public string SelectionRangeDisplay => Buffer is null
        ? "—"
        : $"0x{EffectiveSelectionStart.ToString(OffsetFormat, CultureInfo.InvariantCulture)}  →  0x{EffectiveSelectionEnd.ToString(OffsetFormat, CultureInfo.InvariantCulture)}";

    public string SelectionLengthDisplay
    {
        get
        {
            if (Buffer is null)
            {
                return "";
            }
            var n = EffectiveSelectionEnd - EffectiveSelectionStart + 1;
            return n == 1 ? "1 byte" : $"{n:N0} bytes";
        }
    }

    private void RaiseSelectionPanel()
    {
        OnPropertyChanged(nameof(EffectiveSelectionStart));
        OnPropertyChanged(nameof(EffectiveSelectionEnd));
        OnPropertyChanged(nameof(SelectionRangeDisplay));
        OnPropertyChanged(nameof(SelectionLengthDisplay));
    }

    private void RaiseCaretPanel()
    {
        OnPropertyChanged(nameof(CaretOffsetPadded));
        OnPropertyChanged(nameof(CaretByteHexShort));
        OnPropertyChanged(nameof(CaretByteChar));
        OnPropertyChanged(nameof(CaretByteBinarySpaced));
        RaiseSelectionPanel();
    }

    [RelayCommand]
    private async Task CopyInspectorValueAsync(InspectorRow? row)
    {
        if (row is not null && !row.IsDivider)
        {
            await CopyToClipboardAsync(row.Value);
        }
    }

    [RelayCommand]
    private async Task CopyCaretOffsetAsync() => await CopyToClipboardAsync(CaretOffsetPadded);

    [RelayCommand]
    private async Task CopySelectionAsTextAsync()
    {
        if (Buffer is null)
        {
            return;
        }
        var start = EffectiveSelectionStart;
        var len = (int)Math.Min(EffectiveSelectionEnd - start + 1, 1 << 20);
        var bytes = new byte[len];
        var read = Buffer.Read(start, bytes);
        var sb = new StringBuilder(read);
        for (int i = 0; i < read; i++)
        {
            var c = bytes[i];
            sb.Append(c is >= 0x20 and < 0x7F or 9 or 10 or 13 ? (char)c : '.');
        }
        await CopyToClipboardAsync(sb.ToString());
    }

    [RelayCommand]
    private async Task CopySelectionHexAsync()
    {
        if (Buffer is null)
        {
            return;
        }
        var start = EffectiveSelectionStart;
        var len = (int)Math.Min(EffectiveSelectionEnd - start + 1, 1 << 20);
        var bytes = new byte[len];
        var read = Buffer.Read(start, bytes);
        await CopyToClipboardAsync(Convert.ToHexString(bytes, 0, read));
    }

    // ============================ Toolbar ============================

    public string SizeDisplay => Buffer is null ? "" : FormatSize(Buffer.Length);

    public string SizeTip => Buffer is null ? "" : $"{Buffer.Length:N0} bytes";

    internal static string FormatSize(long n)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double v = n;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0 ? $"{n:N0} B" : $"{v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[u]}";
    }

    /// <summary>Signature label for the file-type chip, e.g. "PNG image" / "Plain text".</summary>
    [ObservableProperty]
    private string? _formatChip;

    /// <summary>Secondary, muted part of the chip: "no signature", "extension mismatch", "0x89504E47".</summary>
    [ObservableProperty]
    private string? _formatChipNote;

    /// <summary>True when the file's content contradicts its extension — the chip turns amber.</summary>
    [ObservableProperty]
    private bool _formatMismatch;

    /// <summary>Show the text pane as UTF-16LE instead of ASCII (Windows registry / LNK / EVTX strings).</summary>
    [ObservableProperty]
    private bool _textIsUtf16;

    [RelayCommand]
    private void SetTextEncoding(string? which) => TextIsUtf16 = string.Equals(which, "utf16", StringComparison.OrdinalIgnoreCase);

    // ---------------------- SHA-256 of the whole file ----------------------

    /// <summary>Files up to this size are hashed automatically when opened; larger ones on request.</summary>
    private const long AutoHashLimit = 2L << 30;

    private CancellationTokenSource? _hashCts;

    /// <summary>"hashing 37 %" while running, null otherwise.</summary>
    [ObservableProperty]
    private string? _hashStatus;

    public bool HashIsRunning => HashStatus is not null;

    public bool CanStartHash => Buffer is not null && QuickHashSha256Full is null && HashStatus is null;

    partial void OnHashStatusChanged(string? value)
    {
        OnPropertyChanged(nameof(HashIsRunning));
        OnPropertyChanged(nameof(CanStartHash));
    }

    partial void OnQuickHashSha256FullChanged(string? value) => OnPropertyChanged(nameof(CanStartHash));

    [RelayCommand]
    private void StartHash() => _ = HashWholeFileAsync();

    // ---------------------- Inline find ----------------------

    [ObservableProperty]
    private string? _findQuery;

    [ObservableProperty]
    private HexSearchKind _findKind = HexSearchKind.Ascii;

    [ObservableProperty]
    private string? _findStatus;

    private int _findIndex = -1;
    private CancellationTokenSource? _findCts;

    public bool FindIsAscii => FindKind == HexSearchKind.Ascii;
    public bool FindIsUtf16 => FindKind == HexSearchKind.Utf16Le;
    public bool FindIsHex => FindKind == HexSearchKind.Hex;
    public bool FindIsRegex => FindKind == HexSearchKind.Regex;

    partial void OnFindKindChanged(HexSearchKind value)
    {
        OnPropertyChanged(nameof(FindIsAscii));
        OnPropertyChanged(nameof(FindIsUtf16));
        OnPropertyChanged(nameof(FindIsHex));
        OnPropertyChanged(nameof(FindIsRegex));
        if (!string.IsNullOrEmpty(FindQuery))
        {
            _ = RunFindAsync();
        }
    }

    [RelayCommand]
    private void SetFindKind(string? kind) => FindKind = kind?.ToLowerInvariant() switch
    {
        "utf16" => HexSearchKind.Utf16Le,
        "hex" => HexSearchKind.Hex,
        "regex" => HexSearchKind.Regex,
        _ => HexSearchKind.Ascii,
    };

    [RelayCommand]
    private async Task RunFindAsync()
    {
        _findCts?.Cancel();
        SearchResults.Clear();
        _findIndex = -1;
        if (Buffer is null || string.IsNullOrWhiteSpace(FindQuery))
        {
            FindStatus = null;
            return;
        }
        var cts = _findCts = new CancellationTokenSource();
        FindStatus = "searching…";
        try
        {
            await SearchAsync(new HexSearchOptions(FindKind, FindQuery!, CaseSensitive: false, StartOffset: 0), cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }
            if (SearchResults.Count == 0)
            {
                FindStatus = "no matches";
                return;
            }
            // Land on the first hit at or after the caret, like every editor's Find.
            var i = 0;
            while (i < SearchResults.Count && SearchResults[i].Offset < CaretOffset)
            {
                i++;
            }
            ShowHit(i < SearchResults.Count ? i : 0);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FindStatus = ex is ArgumentException or FormatException ? "invalid pattern" : "search failed";
        }
    }

    [RelayCommand]
    private void FindNext()
    {
        if (SearchResults.Count > 0)
        {
            ShowHit((_findIndex + 1) % SearchResults.Count);
        }
    }

    [RelayCommand]
    private void FindPrevious()
    {
        if (SearchResults.Count > 0)
        {
            ShowHit((_findIndex - 1 + SearchResults.Count) % SearchResults.Count);
        }
    }

    private void ShowHit(int index)
    {
        _findIndex = index;
        var hit = SearchResults[index];
        JumpTo(hit.Offset);
        SelectionStart = hit.Offset;
        SelectionEnd = hit.Offset + Math.Max(1, hit.Length) - 1;
        var capped = SearchResults.Count >= 1000 ? "1000+" : SearchResults.Count.ToString(CultureInfo.InvariantCulture);
        FindStatus = $"{index + 1} of {capped}";
    }

    [RelayCommand]
    private void ClearFind()
    {
        _findCts?.Cancel();
        FindQuery = null;
        FindStatus = null;
        SearchResults.Clear();
        _findIndex = -1;
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

    /// <summary>True for the buffer whose tab is in front.</summary>
    [ObservableProperty]
    private bool _isActiveTab;

    public string TabTitle => Buffer?.DisplayName ?? "Empty";

    /// <summary>Full path of the open file; identity for de-duplicating tabs.</summary>
    public string? SourcePath { get; private set; }

    public void OpenFile(string path)
    {
        Buffer?.Dispose();
        SourcePath = Path.GetFullPath(path);
        Buffer = new MmapHexBuffer(path);
        ScrollOffset = 0;
        CaretOffset = 0;
        QuickHashSha256 = null;
        QuickHashSha256Full = null;
        AnalyzeHeader(path);
        if (Buffer.Length <= AutoHashLimit)
        {
            _ = HashWholeFileAsync();
        }
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
                FormatChip = "High entropy";
                FormatChipNote = $"entropy {heur.Entropy:F2} · likely encrypted";
                FormatMismatch = true;
                DetectedFormat = $"Probable encrypted container (entropy {heur.Entropy:F2})";
                DetectedFormatBadge = "⚠ High-entropy data — likely TrueCrypt / VeraCrypt container or already-encrypted blob";
                DetectedRouting = "→ Cannot parse without passphrase";
                return;
            }
            DetectedFormat = "Unknown";
            DetectedFormatBadge = null;
            DetectedRouting = null;
            FormatChip = LooksLikeText(header.AsSpan(0, read)) ? "Plain text" : "Binary data";
            FormatChipNote = "no signature";
            FormatMismatch = false;
            return;
        }
        var best = hits[0];
        var matchedBytes = FormatMatchedBytes(header, best.Signature, read);
        DetectedFormat = $"{best.Signature.Label} ({matchedBytes} at 0x{best.Offset:X})";
        DetectedFormatBadge = _scanner.IsExtensionMismatch(path, header.AsSpan(0, read), out _)
            ? $"⚠ Extension mismatch — content looks like .{best.Signature.Extension}"
            : null;
        DetectedRouting = RouteForExtension(best.Signature.Extension);
        FormatChip = best.Signature.Label;
        FormatMismatch = DetectedFormatBadge is not null;
        FormatChipNote = FormatMismatch ? $"extension says otherwise · .{best.Signature.Extension}" : matchedBytes;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        var sample = head[..Math.Min(head.Length, 8192)];
        if (sample.Length == 0)
        {
            return false;
        }
        var printable = 0;
        foreach (var b in sample)
        {
            if (b is >= 0x20 and < 0x7F or 9 or 10 or 13)
            {
                printable++;
            }
            else if (b == 0)
            {
                return false;
            }
        }
        return printable >= sample.Length * 0.95;
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

    /// <summary>
    /// SHA-256 over the whole file, streamed in the background with progress and cancelled if the
    /// tab changes. The toolbar chip only ever shows a digest that covers every byte — a prefix
    /// hash labelled "SHA-256" would be a false statement about the evidence.
    /// </summary>
    private async Task HashWholeFileAsync()
    {
        var buffer = Buffer;
        if (buffer is null)
        {
            return;
        }
        _hashCts?.Cancel();
        var cts = _hashCts = new CancellationTokenSource();
        var ct = cts.Token;
        HashStatus = "hashing 0 %";
        try
        {
            var digest = await Task.Run(() =>
            {
                using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                var chunk = new byte[4 << 20];
                long pos = 0, len = buffer.Length;
                var lastReport = Environment.TickCount64;
                while (pos < len)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = buffer.Read(pos, chunk.AsSpan(0, (int)Math.Min(chunk.Length, len - pos)));
                    if (n <= 0)
                    {
                        break;
                    }
                    sha.AppendData(chunk, 0, n);
                    pos += n;
                    if (Environment.TickCount64 - lastReport > 150)
                    {
                        lastReport = Environment.TickCount64;
                        var pct = (int)(pos * 100 / Math.Max(1, len));
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!ct.IsCancellationRequested)
                            {
                                HashStatus = $"hashing {pct} %";
                            }
                        });
                    }
                }
                return Convert.ToHexStringLower(sha.GetHashAndReset());
            }, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(buffer, Buffer))
            {
                return;
            }
            QuickHashSha256Full = digest;
            QuickHashSha256 = digest[..8] + "…";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            QuickHashSha256 = null;
            QuickHashSha256Full = null;
        }
        finally
        {
            if (ReferenceEquals(_hashCts, cts))
            {
                HashStatus = null;
            }
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
        _hashCts?.Cancel();
        _findCts?.Cancel();
        Buffer?.Dispose();
        Buffer = null;
        SourcePath = null;
        QuickHashSha256 = null;
        QuickHashSha256Full = null;
        FormatChip = null;
        FormatChipNote = null;
    }
}
