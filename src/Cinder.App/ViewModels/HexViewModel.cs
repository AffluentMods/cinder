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
        DetectedFormat = $"{best.Signature.Label} (.{best.Signature.Extension})";
        DetectedFormatBadge = _scanner.IsExtensionMismatch(path, header.AsSpan(0, read), out _)
            ? $"⚠ Extension mismatch — content looks like .{best.Signature.Extension}"
            : null;
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
            QuickHashSha256 = result.Sha256?[..16] + "…";
        }
        catch
        {
            QuickHashSha256 = null;
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
            if (!long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out parsed))
            {
                return;
            }
        }
        else if (!long.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out parsed))
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

    public void Dispose()
    {
        Buffer?.Dispose();
        Buffer = null;
    }
}
