using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cinder.App.Controls;

/// <summary>
/// The "nothing here yet" panel every tool shows before it has data: an icon, what the tool is
/// waiting for, one sentence on what it will do, and (as content) the action that gets it going.
/// One control so the 40-odd tools cannot drift apart on spacing, type or tone.
/// </summary>
public class EmptyState : ContentControl
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<EmptyState, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Title));

    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Message));

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
}
