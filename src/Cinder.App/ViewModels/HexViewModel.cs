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
    }

    partial void OnSelectedSearchResultChanged(HexSearchHit? value)
    {
        if (value is null || Buffer is null)
        {
            return;
        }
        CaretOffset = value.Offset;
        ScrollOffset = (value.Offset / BytesPerRow) * BytesPerRow;
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
            DetectedFormat = "Unknown";
            DetectedFormatBadge = null;
            return;
        }
        var best = hits[0];
        var matchedBytes = FormatMatchedBytes(header, best.Signature, read);
        DetectedFormat = $"{best.Signature.Label} ({matchedBytes} at 0x{best.Offset:X})";
        DetectedFormatBadge = _scanner.IsExtensionMismatch(path, header.AsSpan(0, read), out _)
            ? $"⚠ Extension mismatch — content looks like .{best.Signature.Extension}"
            : null;
    }

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
            var result = await _hash.ComputeAsync(ms, [HashAlgorithmKind.Sha256]).ConfigureAwait(false);
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
                await clipboard.SetTextAsync(QuickHashSha256Full).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort.
        }
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
        if (parsed < 0 || parsed >= Buffer.Length)
        {
            return;
        }
        CaretOffset = parsed;
        ScrollOffset = (parsed / BytesPerRow) * BytesPerRow;
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
        }, ct).ConfigureAwait(false);

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
