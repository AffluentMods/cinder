using System.Globalization;
using Avalonia.Data.Converters;
using Cinder.App.ViewModels.Tools;

namespace Cinder.App.Views.Tools;

public static class ToolKindConverters
{
    private static readonly HashSet<string> Specialized = new(StringComparer.Ordinal)
    {
        "hex", "reports", "timeline", "search", "ai",
    };

    public static readonly IValueConverter IsGeneric = new FuncValueConverter<ToolViewModel?, bool>(
        t => t is not null && !Specialized.Contains(t.Kind));
}

internal sealed class FuncValueConverter<TIn, TOut> : IValueConverter
{
    private readonly Func<TIn?, TOut?> _fn;
    public FuncValueConverter(Func<TIn?, TOut?> fn) => _fn = fn;
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        _fn(value is TIn t ? t : default);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
