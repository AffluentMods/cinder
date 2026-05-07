namespace Cinder.Carving;

/// <summary>Built-in carving rules. ~30 covers the forensic-relevant common cases; users add
/// their own via the case settings UI.</summary>
public static class CarveSignatures
{
    public static IReadOnlyList<CarveSignature> Defaults { get; } =
    [
        new("JPEG", "jpg", [0xFF, 0xD8, 0xFF], [0xFF, 0xD9], 30 * 1024 * 1024, ValidateJpeg),
        new("PNG", "png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82], 80 * 1024 * 1024, ValidatePng),
        new("GIF", "gif", "GIF8"u8.ToArray(), [0x00, 0x3B], 10 * 1024 * 1024, null),
        new("BMP", "bmp", "BM"u8.ToArray(), null, 50 * 1024 * 1024, null),
        new("TIFF", "tiff", [0x49, 0x49, 0x2A, 0x00], null, 100 * 1024 * 1024, null),
        new("PDF", "pdf", "%PDF-"u8.ToArray(), "%%EOF"u8.ToArray(), 100 * 1024 * 1024, null),
        new("ZIP", "zip", [0x50, 0x4B, 0x03, 0x04], null, 500 * 1024 * 1024, null),
        new("RAR", "rar", "Rar!"u8.ToArray(), null, 1024 * 1024 * 1024, null),
        new("7Z", "7z", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], null, 1024 * 1024 * 1024, null),
        new("MP3", "mp3", "ID3"u8.ToArray(), null, 50 * 1024 * 1024, null),
        new("MP4", "mp4", "ftyp"u8.ToArray(), null, 4L * 1024 * 1024 * 1024, null),
        new("DOCX", "docx", [0x50, 0x4B, 0x03, 0x04], null, 50 * 1024 * 1024, null),
        new("OLE Compound (DOC/XLS)", "doc", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], null, 100 * 1024 * 1024, null),
        new("RTF", "rtf", "{\\rtf"u8.ToArray(), "}"u8.ToArray(), 50 * 1024 * 1024, null),
        new("WAV", "wav", "RIFF"u8.ToArray(), null, 500 * 1024 * 1024, null),
        new("AVI", "avi", "RIFF"u8.ToArray(), null, 4L * 1024 * 1024 * 1024, null),
        new("EXE/DLL (PE)", "exe", "MZ"u8.ToArray(), null, 200 * 1024 * 1024, ValidatePe),
        new("ELF", "elf", [0x7F, 0x45, 0x4C, 0x46], null, 200 * 1024 * 1024, null),
        new("SQLite", "sqlite", "SQLite format 3\0"u8.ToArray(), null, 10L * 1024 * 1024 * 1024, null),
        new("PST", "pst", "!BDN"u8.ToArray(), null, 50L * 1024 * 1024 * 1024, null),
        new("Registry hive", "hive", "regf"u8.ToArray(), null, 500 * 1024 * 1024, null),
        new("LNK", "lnk", [0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00], null, 64 * 1024, null),
        new("EVTX", "evtx", "ElfFile\0"u8.ToArray(), null, 500 * 1024 * 1024, null),
        new("PCAP", "pcap", [0xD4, 0xC3, 0xB2, 0xA1], null, 4L * 1024 * 1024 * 1024, null),
        new("PCAPNG", "pcapng", [0x0A, 0x0D, 0x0D, 0x0A], null, 4L * 1024 * 1024 * 1024, null),
        new("HEIC", "heic", "ftypheic"u8.ToArray(), null, 100 * 1024 * 1024, null),
        new("WebP", "webp", "WEBP"u8.ToArray(), null, 50 * 1024 * 1024, null),
        new("Matroska/WebM", "mkv", [0x1A, 0x45, 0xDF, 0xA3], null, 10L * 1024 * 1024 * 1024, null),
        new("Outlook MSG", "msg", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], null, 100 * 1024 * 1024, null),
    ];

    private static bool ValidateJpeg(ReadOnlyMemory<byte> blob)
    {
        var s = blob.Span;
        return s.Length > 4 && s[0] == 0xFF && s[1] == 0xD8 &&
               s[^2] == 0xFF && s[^1] == 0xD9;
    }

    private static bool ValidatePng(ReadOnlyMemory<byte> blob)
    {
        var s = blob.Span;
        if (s.Length < 16)
        {
            return false;
        }
        return s[0] == 0x89 && s[1] == 0x50 && s[2] == 0x4E && s[3] == 0x47 &&
               s[^8] == 0x49 && s[^7] == 0x45 && s[^6] == 0x4E && s[^5] == 0x44;
    }

    private static bool ValidatePe(ReadOnlyMemory<byte> blob)
    {
        var s = blob.Span;
        if (s.Length < 0x40)
        {
            return false;
        }
        var peOff = BitConverter.ToInt32(s.Slice(0x3C, 4));
        return peOff > 0 && peOff < s.Length - 4 &&
               s[peOff] == (byte)'P' && s[peOff + 1] == (byte)'E' && s[peOff + 2] == 0 && s[peOff + 3] == 0;
    }
}
