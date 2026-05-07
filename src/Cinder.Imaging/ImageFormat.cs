namespace Cinder.Imaging;

public enum ImageFormat
{
    Raw,    // .dd / .img — flat
    Ewf,    // .E01 — libewf, segmented + compressed + hashed
    Aff4,   // .af4 — AFF4 with optional compression
    Vhd,    // Microsoft VHD (footer-based)
    Vhdx,   // Microsoft VHDX (block-based)
}

public static class ImageFormatExtensions
{
    public static string DefaultExtension(this ImageFormat fmt) => fmt switch
    {
        ImageFormat.Raw => ".dd",
        ImageFormat.Ewf => ".E01",
        ImageFormat.Aff4 => ".af4",
        ImageFormat.Vhd => ".vhd",
        ImageFormat.Vhdx => ".vhdx",
        _ => ".bin",
    };
}
