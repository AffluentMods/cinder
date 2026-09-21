using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Cinder.Hex;

/// <summary>
/// Whole-file entropy strip beside the hex grid, doubling as its scrollbar.
///
/// <para>Each band is the Shannon entropy of a sample from its slice of the file. Brightness
/// rises with entropy; bands at or above 7.2 bits/byte — the range of compressed and encrypted
/// data — take the violet "high" hue, and all-zero bands draw nothing. An examiner can see an
/// embedded archive, a VeraCrypt volume or a wiped region before scrolling to it.</para>
///
/// <para>Sampling keeps cost flat: at most 4 KiB per band, bands ≈ one per 2 px, computed once
/// per buffer off the UI thread. Click or drag moves the view; the rectangle is the viewport.</para>
/// </summary>
public sealed class HexMinimap : Control
{
    public static readonly StyledProperty<IHexBuffer?> BufferProperty =
        AvaloniaProperty.Register<HexMinimap, IHexBuffer?>(nameof(Buffer));

    public static readonly StyledProperty<long> ScrollOffsetProperty =
        AvaloniaProperty.Register<HexMinimap, long>(nameof(ScrollOffset), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> BytesPerRowProperty =
        AvaloniaProperty.Register<HexMinimap, int>(nameof(BytesPerRow), 16);

    public static readonly StyledProperty<int> VisibleRowsProperty =
        AvaloniaProperty.Register<HexMinimap, int>(nameof(VisibleRows), 32);

    public IHexBuffer? Buffer { get => GetValue(BufferProperty); set => SetValue(BufferProperty, value); }
    public long ScrollOffset { get => GetValue(ScrollOffsetProperty); set => SetValue(ScrollOffsetProperty, value); }
    public int BytesPerRow { get => GetValue(BytesPerRowProperty); set => SetValue(BytesPerRowProperty, value); }
    public int VisibleRows { get => GetValue(VisibleRowsProperty); set => SetValue(VisibleRowsProperty, value); }

    private const int SampleBytes = 4096;
    private const double BandPx = 2;

    private float[]? _entropy;      // bits/byte per band, -1 = all zero
    private IBrush?[]? _bandBrushes; // precomputed once per buffer — Render runs on every scroll tick
    private int _computedBands;
    private IHexBuffer? _computedFor;
    private CancellationTokenSource? _cts;
    private bool _dragging;

    private static readonly IBrush Track = new SolidColorBrush(Color.FromRgb(0x0A, 0x0B, 0x0E));
    private static readonly IBrush ViewportFill = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
    private static readonly IPen ViewportPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IPen EdgePen = new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x2A)), 1);

    static HexMinimap()
    {
        AffectsRender<HexMinimap>(ScrollOffsetProperty, BytesPerRowProperty, VisibleRowsProperty);
        BufferProperty.Changed.AddClassHandler<HexMinimap>((m, _) => m.Recompute());
        BoundsProperty.Changed.AddClassHandler<HexMinimap>((m, e) =>
        {
            if (e.OldValue is Rect o && e.NewValue is Rect n && Math.Abs(o.Height - n.Height) > 8)
            {
                m.Recompute();
            }
        });
    }

    public HexMinimap()
    {
        Width = 14;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Entropy map — brighter is denser data; violet (≥ 7.2 bits/byte) is typically compressed or encrypted; dark is zeros. Click or drag to jump.");
    }

    private void Recompute()
    {
        _cts?.Cancel();
        var buffer = Buffer;
        var bands = Math.Max(16, (int)(Bounds.Height / BandPx));
        if (buffer is null || buffer.Length == 0 || Bounds.Height <= 0)
        {
            _entropy = null;
            InvalidateVisual();
            return;
        }
        if (ReferenceEquals(buffer, _computedFor) && bands == _computedBands && _entropy is not null)
        {
            return;
        }
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        Task.Run(() =>
        {
            var result = new float[bands];
            var sample = new byte[SampleBytes];
            Span<int> counts = stackalloc int[256];
            var len = buffer.Length;
            for (int i = 0; i < bands; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                var start = (long)((double)len * i / bands);
                var n = buffer.Read(start, sample.AsSpan(0, (int)Math.Min(SampleBytes, len - start)));
                result[i] = Entropy(sample.AsSpan(0, Math.Max(0, n)), counts);
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested || !ReferenceEquals(buffer, Buffer))
                {
                    return;
                }
                _entropy = result;
                _bandBrushes = result.Select(BandBrush).ToArray();
                _computedBands = bands;
                _computedFor = buffer;
                InvalidateVisual();
            });
        }, ct);
    }

    private static float Entropy(ReadOnlySpan<byte> data, Span<int> counts)
    {
        if (data.Length == 0)
        {
            return -1;
        }
        counts.Clear();
        var zero = true;
        foreach (var b in data)
        {
            counts[b]++;
            zero &= b == 0;
        }
        if (zero)
        {
            return -1;
        }
        double h = 0;
        foreach (var c in counts)
        {
            if (c > 0)
            {
                var p = (double)c / data.Length;
                h -= p * Math.Log2(p);
            }
        }
        return (float)h;
    }

    private static IBrush? BandBrush(float h)
    {
        if (h < 0)
        {
            return null;   // zeros: leave the track dark
        }
        var c = h >= 7.2f
            ? Color.FromArgb((byte)Math.Min(230, 120 + (h - 7.2f) / 0.8f * 110), 0x9B, 0x8E, 0xE0)
            : Color.FromArgb((byte)(24 + h / 7.2f * 120), 0xE7, 0xE9, 0xEE);
        return new ImmutableSolidColorBrush(c);
    }

    public override void Render(DrawingContext context)
    {
        var b = Bounds;
        context.FillRectangle(Track, new Rect(0, 0, b.Width, b.Height));
        context.DrawLine(EdgePen, new Point(0.5, 0), new Point(0.5, b.Height));
        var buffer = Buffer;
        if (buffer is null || buffer.Length == 0)
        {
            return;
        }

        if (_bandBrushes is { } bands)
        {
            var bandH = b.Height / bands.Length;
            for (int i = 0; i < bands.Length; i++)
            {
                if (bands[i] is { } brush)
                {
                    context.FillRectangle(brush, new Rect(3, i * bandH, b.Width - 5, Math.Max(1, bandH - 0.5)));
                }
            }
        }

        // Viewport indicator.
        var bpr = Math.Max(1, BytesPerRow);
        var totalRows = Math.Max(1.0, Math.Ceiling((double)buffer.Length / bpr));
        var top = (ScrollOffset / bpr) / totalRows * b.Height;
        var height = Math.Max(6, VisibleRows / totalRows * b.Height);
        var rect = new Rect(1, Math.Min(top, b.Height - height), b.Width - 1, Math.Min(height, b.Height));
        context.DrawRectangle(ViewportFill, ViewportPen, rect);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        JumpTo(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            JumpTo(e.GetPosition(this).Y);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void JumpTo(double y)
    {
        var buffer = Buffer;
        if (buffer is null || Bounds.Height <= 0)
        {
            return;
        }
        var bpr = Math.Max(1, BytesPerRow);
        var totalRows = (buffer.Length + bpr - 1) / bpr;
        var row = (long)(Math.Clamp(y / Bounds.Height, 0, 1) * totalRows) - VisibleRows / 2;
        row = Math.Clamp(row, 0, Math.Max(0, totalRows - VisibleRows));
        ScrollOffset = row * bpr;
    }
}
