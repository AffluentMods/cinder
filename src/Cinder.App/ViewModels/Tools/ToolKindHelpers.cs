using Avalonia.Data.Converters;

namespace Cinder.App.ViewModels.Tools;

/// <summary>Tool-kind predicates used by view templates to branch presentation.</summary>
public static class ToolKindHelpers
{
    public static readonly IValueConverter HasSidecar =
        new global::Cinder.App.Views.Tools.FuncValueConverter<ToolViewModel?, bool>(
            t => t is SidecarToolViewModel);
}
