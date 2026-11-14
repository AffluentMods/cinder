using Avalonia;
using Avalonia.Controls;

namespace Cinder.App.Controls;

/// <summary>
/// The layout for a tool that runs one job from a short form (image, convert, verify, mount,
/// look up). The form sits in a fixed column on the left; <see cref="Aside"/> fills the rest of
/// the pane with what the job will do, its progress, and its result. Without this split a
/// six-field form sat in one corner of an otherwise blank screen.
/// </summary>
public class TaskPage : ContentControl
{
    public static readonly StyledProperty<object?> AsideProperty =
        AvaloniaProperty.Register<TaskPage, object?>(nameof(Aside));

    public static readonly StyledProperty<double> FormWidthProperty =
        AvaloniaProperty.Register<TaskPage, double>(nameof(FormWidth), 460);

    /// <summary>What fills the right-hand side: steps, progress, result.</summary>
    public object? Aside
    {
        get => GetValue(AsideProperty);
        set => SetValue(AsideProperty, value);
    }

    public double FormWidth
    {
        get => GetValue(FormWidthProperty);
        set => SetValue(FormWidthProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AsideProperty)
        {
            // Keep the aside in the logical tree so it inherits the DataContext and resources
            // the same way the form (the Content) does.
            if (change.OldValue is Control old)
            {
                LogicalChildren.Remove(old);
            }
            if (change.NewValue is Control added && !LogicalChildren.Contains(added))
            {
                LogicalChildren.Add(added);
            }
        }
    }
}

/// <summary>One numbered step in a "what happens" list: a number, a short title, a sentence.</summary>
public class Step : Avalonia.Controls.Primitives.TemplatedControl
{
    public static readonly StyledProperty<string?> NumberProperty =
        AvaloniaProperty.Register<Step, string?>(nameof(Number));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<Step, string?>(nameof(Title));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Step, string?>(nameof(Text));

    public string? Number
    {
        get => GetValue(NumberProperty);
        set => SetValue(NumberProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
