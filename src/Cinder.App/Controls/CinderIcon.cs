using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Cinder.App.Controls;

/// <summary>
/// Outline icon drawn from a 24×24 stroke geometry (Lucide conventions: 1.6 px stroke, round caps
/// and joins). Unlike <see cref="PathIcon"/>, which fills its geometry, this strokes it — which is
/// what line icons need — and keeps the stroke weight constant in device pixels at any size, so a
/// 14 px toolbar icon and a 20 px empty-state icon read as the same family.
/// </summary>
public sealed class CinderIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<CinderIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<CinderIcon>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<CinderIcon, double>(nameof(StrokeThickness), 1.6);

    static CinderIcon()
    {
        AffectsRender<CinderIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
    }

    public CinderIcon()
    {
        Width = 16;
        Height = 16;
        IsHitTestVisible = false;
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var data = Data;
        var brush = Foreground;
        if (data is null || brush is null)
        {
            return;
        }
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }
        var scale = size / 24.0;
        var dx = (Bounds.Width - size) / 2;
        var dy = (Bounds.Height - size) / 2;
        var pen = new Pen(brush, StrokeThickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
        {
            context.DrawGeometry(null, pen, data);
        }
    }
}
