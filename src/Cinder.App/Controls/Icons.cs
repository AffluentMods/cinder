using System.Globalization;
using Avalonia.Media;

namespace Cinder.App.Controls;

/// <summary>
/// Cinder's icon set: one family of 24×24 stroke geometries (Lucide conventions), parsed once.
/// Rendered by <see cref="CinderIcon"/>. XAML reaches them through <c>{x:Static c:Icons.Hex}</c>;
/// tools reach theirs through <see cref="ForTool"/>.
///
/// <para>Why not emoji: they render in colour, at different optical sizes and weights per OS
/// (Segoe UI Emoji vs Noto), and cannot take the theme's foreground. A forensic workstation's
/// rail is scanned hundreds of times a day; the icons need to be one quiet, consistent family.</para>
/// </summary>
public static class Icons
{
    // ---- primitives ------------------------------------------------------------------------

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Circle(double cx, double cy, double r) =>
        $"M{F(cx - r)} {F(cy)} A{F(r)} {F(r)} 0 1 0 {F(cx + r)} {F(cy)} A{F(r)} {F(r)} 0 1 0 {F(cx - r)} {F(cy)} Z ";

    private static string Rect(double x, double y, double w, double h, double r) =>
        $"M{F(x + r)} {F(y)} H{F(x + w - r)} A{F(r)} {F(r)} 0 0 1 {F(x + w)} {F(y + r)} V{F(y + h - r)} " +
        $"A{F(r)} {F(r)} 0 0 1 {F(x + w - r)} {F(y + h)} H{F(x + r)} A{F(r)} {F(r)} 0 0 1 {F(x)} {F(y + h - r)} " +
        $"V{F(y + r)} A{F(r)} {F(r)} 0 0 1 {F(x + r)} {F(y)} Z ";

    private static Geometry G(string data) => StreamGeometry.Parse(data.Trim());

    // ---- set ---------------------------------------------------------------------------------

    public static readonly Geometry Home = G("M4 11 L12 4.5 L20 11 M6 9.5 V20 H18 V9.5");
    public static readonly Geometry Hex = G("M8 4 H5 V20 H8 M16 4 H19 V20 H16 M10 9 L14 15 M14 9 L10 15");
    public static readonly Geometry Text = G("M4 6 H20 M4 12 H14 M4 18 H17");
    public static readonly Geometry Image = G(Rect(3, 5, 18, 14, 2) + Circle(9, 10, 1.5) + "M21 16 L16 11 L8 19");
    public static readonly Geometry Doc = G("M7 3 H14 L19 8 V21 H7 Z M14 3 V8 H19");
    public static readonly Geometry Folder = G("M3 6.5 H9 L11 8.5 H21 V19 H3 Z");
    public static readonly Geometry List = G(Rect(5, 3, 14, 18, 2) + "M9 8 H15 M9 12 H15 M9 16 H13");
    public static readonly Geometry Grid = G(Rect(4, 4, 7, 7, 1) + Rect(13, 4, 7, 7, 1) + Rect(4, 13, 7, 7, 1) + Rect(13, 13, 7, 7, 1));
    public static readonly Geometry Pulse = G("M3 12 H7 L10 5 L14 19 L17 12 H21");
    public static readonly Geometry Clock = G(Circle(12, 12, 9) + "M12 7 V12 L15 14");
    public static readonly Geometry Link = G("M10 14 A4 4 0 0 0 15.7 14 L18.7 11 A4 4 0 0 0 13 5.3 L12 6.3 M14 10 A4 4 0 0 0 8.3 10 L5.3 13 A4 4 0 0 0 11 18.7 L12 17.7");
    public static readonly Geometry Globe = G(Circle(12, 12, 9) + "M3 12 H21 M12 3 A14 14 0 0 1 12 21 A14 14 0 0 1 12 3");
    public static readonly Geometry Usb = G("M12 3 V17 M9.5 5.5 L12 3 L14.5 5.5 M8 9 V12 L12 14.5 M16 8 V11 L12 13.5 " + Circle(12, 19, 2));
    public static readonly Geometry Wifi = G("M4.5 10.5 A11 11 0 0 1 19.5 10.5 M7.5 14 A6.5 6.5 0 0 1 16.5 14 " + Circle(12, 18, 1));
    public static readonly Geometry Gauge = G("M4 17 A8 8 0 1 1 20 17 M12 14 L16 10");
    public static readonly Geometry Stack = G("M12 3 L21 8 L12 13 L3 8 Z M3 13 L12 18 L21 13");
    public static readonly Geometry Trash = G("M4 7 H20 M9 7 V4 H15 V7 M6 7 L7 20 H17 L18 7");
    public static readonly Geometry Mail = G(Rect(3, 5, 18, 14, 2) + "M3 7 L12 13 L21 7");
    public static readonly Geometry Terminal = G(Rect(3, 4, 18, 16, 2) + "M7 9 L10 12 L7 15 M12 15 H17");
    public static readonly Geometry Chip = G(Rect(6, 6, 12, 12, 1.5) + "M9 3 V6 M15 3 V6 M9 18 V21 M15 18 V21 M3 9 H6 M3 15 H6 M18 9 H21 M18 15 H21");
    public static readonly Geometry Network = G(Circle(5, 12, 2) + Circle(19, 6, 2) + Circle(19, 18, 2) + "M7 11 L17 7 M7 13 L17 17");
    public static readonly Geometry Phone = G(Rect(7, 3, 10, 18, 2) + "M11 18 H13");
    public static readonly Geometry Pin = G("M12 21 C12 21 6 15.5 6 10 A6 6 0 0 1 18 10 C18 15.5 12 21 12 21 Z " + Circle(12, 10, 2));
    public static readonly Geometry Search = G(Circle(11, 11, 7) + "M20 20 L16 16");
    public static readonly Geometry Hash = G("M5 9 H19 M5 15 H19 M10 4 L8 20 M16 4 L14 20");
    public static readonly Geometry Shield = G("M12 3 L20 6 V12 C20 17 16.5 20 12 21 C7.5 20 4 17 4 12 V6 Z");
    public static readonly Geometry Target = G(Circle(12, 12, 8) + Circle(12, 12, 3));
    public static readonly Geometry Spark = G("M12 3 V7 M12 17 V21 M3 12 H7 M17 12 H21 M6 6 L8.5 8.5 M15.5 15.5 L18 18 M6 18 L8.5 15.5 M15.5 8.5 L18 6");
    public static readonly Geometry Disk = G(Rect(3, 6, 18, 12, 2) + "M7 12 H7.01 M11 12 H17");
    public static readonly Geometry Check = G("M5 12.5 L9 16.5 L19 6.5");
    public static readonly Geometry Mount = G("M4 14 H20 V20 H4 Z M12 3 V11 M8 7 L12 11 L16 7");
    public static readonly Geometry Swap = G("M4 8 H19 L16 5 M20 16 H5 L8 19");
    public static readonly Geometry Cut = G(Circle(6, 6, 3) + Circle(6, 18, 3) + "M8.5 8 L20 18 M8.5 16 L20 6");
    public static readonly Geometry Cloud = G("M7 18 A4 4 0 0 1 6.4 10 A6 6 0 0 1 18 9.5 A4.3 4.3 0 0 1 17 18 Z");
    public static readonly Geometry Cog = G(Circle(12, 12, 3) + "M12 3 V5.5 M12 18.5 V21 M3 12 H5.5 M18.5 12 H21 M5.6 5.6 L7.4 7.4 M16.6 16.6 L18.4 18.4 M5.6 18.4 L7.4 16.6 M16.6 7.4 L18.4 5.6");
    public static readonly Geometry Bookmark = G("M6 3 H18 V21 L12 17 L6 21 Z");
    public static readonly Geometry Chevron = G("M9 6 L15 12 L9 18");
    public static readonly Geometry ChevronDown = G("M6 9 L12 15 L18 9");
    public static readonly Geometry Copy = G(Rect(8, 8, 12, 12, 2) + "M16 8 V5 A1 1 0 0 0 15 4 H5 A1 1 0 0 0 4 5 V15 A1 1 0 0 0 5 16 H8");
    public static readonly Geometry Close = G("M6 6 L18 18 M18 6 L6 18");
    public static readonly Geometry Plus = G("M12 5 V19 M5 12 H19");
    public static readonly Geometry Help = G(Circle(12, 12, 9) + "M9.5 9.2 A2.5 2.5 0 1 1 13.1 11.4 C12.4 11.8 12 12.3 12 13 V13.5 M12 17 H12.01");
    public static readonly Geometry Alert = G("M12 4 L21 20 H3 Z M12 10 V14 M12 17 H12.01");
    public static readonly Geometry Goto = G("M5 5 V13 H18 M14 9 L18 13 L14 17");
    public static readonly Geometry Out = G("M7 17 L17 7 M9 7 H17 V15");
    public static readonly Geometry Lock = G(Rect(5, 11, 14, 10, 2) + "M8 11 V7 A4 4 0 0 1 16 7 V11");
    public static readonly Geometry Workflow = G(Rect(3, 4, 6, 6, 1) + Rect(15, 14, 6, 6, 1) + "M9 7 H13 A2 2 0 0 1 15 9 V14");
    public static readonly Geometry Puzzle = G("M9 4 H13 V6 A2 2 0 0 0 17 6 V4 H20 V11 H18 A2 2 0 0 0 18 15 H20 V20 H13 V18 A2 2 0 0 0 9 18 V20 H4 V13 H6 A2 2 0 0 0 6 9 H4 V4 Z");
    public static readonly Geometry Report = G("M7 3 H14 L19 8 V21 H7 Z M14 3 V8 H19 M10 13 H16 M10 17 H14");
    public static readonly Geometry Camera = G("M4 8 H7 L9 5 H15 L17 8 H20 V19 H4 Z " + Circle(12, 13, 3.5));
    public static readonly Geometry Graph = G(Circle(6, 6, 2) + Circle(18, 6, 2) + Circle(12, 18, 2) + "M8 6 H16 M7 8 L11 16 M17 8 L13 16");
    public static readonly Geometry Download = G("M12 4 V15 M7 10 L12 15 L17 10 M5 20 H19");
    public static readonly Geometry Filter = G("M4 5 H20 L14 12.5 V19 L10 17 V12.5 Z");
    public static readonly Geometry Minimize = G("M5 12 H19");
    public static readonly Geometry Maximize = G(Rect(5, 5, 14, 14, 1));
    public static readonly Geometry Sidebar = G(Rect(3, 4, 18, 16, 2) + "M9 4 V20");
    public static readonly Geometry Panel = G(Rect(3, 4, 18, 16, 2) + "M15 4 V20");

    /// <summary>The icon a tool shows in the rail, its header and its empty state.</summary>
    public static Geometry ForTool(string id) => id switch
    {
        "dashboard" => Home,
        "hex" => Hex,
        "strings" => Text,
        "gallery" => Image,
        "documents" => Doc,
        "filesystem" => Folder,
        "usnjrnl" or "logfile" => List,
        "registry" => Grid,
        "evtx" => Pulse,
        "prefetch" => Clock,
        "shellbags" => Folder,
        "jumplists" or "lnk" => Link,
        "browser" => Globe,
        "usb" => Usb,
        "wifi" => Wifi,
        "srum" => Gauge,
        "amcache" or "shimcache" => Stack,
        "recyclebin" => Trash,
        "email" => Mail,
        "linux" => Terminal,
        "memory" or "ramcapture" => Chip,
        "network" => Network,
        "mobile" => Phone,
        "timeline" => Clock,
        "map" => Pin,
        "graph" => Graph,
        "search" => Search,
        "hashsets" => Hash,
        "yara" => Shield,
        "ioc" => Target,
        "virustotal" => Globe,
        "ai" => Spark,
        "imager" => Disk,
        "verify" => Check,
        "mount" => Mount,
        "convert" => Swap,
        "shadowcopy" => Camera,
        "carver" => Cut,
        "cloud" => Cloud,
        "cases" => Folder,
        "bookmarks" => Bookmark,
        "reports" => Report,
        "custody" => Lock,
        "workflows" => Workflow,
        "plugins" => Puzzle,
        "settings" => Cog,
        _ => Doc,
    };
}
