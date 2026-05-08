using Avalonia.Data.Converters;

namespace Cinder.App.ViewModels.Tools;

public static class ToolKindHelpers
{
    private static readonly HashSet<string> SidecarKinds = new(StringComparer.Ordinal)
    {
        "filesystem", "registry", "evtx", "prefetch", "shellbags", "jumplists", "lnk",
        "browser", "usb", "wifi", "srum", "amcache", "shimcache", "email",
        "linux", "memory", "network", "mobile",
    };

    private static readonly HashSet<string> AcquisitionKinds = new(StringComparer.Ordinal)
    {
        "imager", "verify", "mount", "convert", "shadowcopy", "ramcapture", "carver", "cloud",
    };

    private static readonly HashSet<string> ManagementKinds = new(StringComparer.Ordinal)
    {
        "cases", "custody", "workflows", "plugins", "settings",
        "strings", "gallery", "documents", "map", "graph", "hashsets", "yara", "virustotal",
    };

    public static readonly IValueConverter IsSidecar = new global::Cinder.App.Views.Tools.FuncValueConverter<ToolViewModel?, bool>(
        t => t is not null && SidecarKinds.Contains(t.Kind));

    public static readonly IValueConverter IsAcquisition = new global::Cinder.App.Views.Tools.FuncValueConverter<ToolViewModel?, bool>(
        t => t is not null && AcquisitionKinds.Contains(t.Kind));

    public static readonly IValueConverter IsManagement = new global::Cinder.App.Views.Tools.FuncValueConverter<ToolViewModel?, bool>(
        t => t is not null && ManagementKinds.Contains(t.Kind));
}
