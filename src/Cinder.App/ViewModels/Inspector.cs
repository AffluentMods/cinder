using System.Buffers.Binary;
using System.Globalization;

namespace Cinder.App.ViewModels;

/// <summary>
/// Decodes the bytes at the caret as common integer / float / well-known struct types in both
/// endiannesses. Returns "—" when there aren't enough bytes left in the buffer for a given type.
/// </summary>
public static class Inspector
{
    public static InspectorRow[] Decode(ReadOnlySpan<byte> b)
    {
        var rows = new InspectorRow[12];
        rows[0] = new("int8",          b.Length >= 1 ? ((sbyte)b[0]).ToString(CultureInfo.InvariantCulture) : "—");
        rows[1] = new("uint8",         b.Length >= 1 ? b[0].ToString(CultureInfo.InvariantCulture) : "—");
        rows[2] = new("int16 LE",      b.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(b).ToString(CultureInfo.InvariantCulture) : "—");
        rows[3] = new("int16 BE",      b.Length >= 2 ? BinaryPrimitives.ReadInt16BigEndian(b).ToString(CultureInfo.InvariantCulture) : "—");
        rows[4] = new("int32 LE",      b.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(b).ToString(CultureInfo.InvariantCulture) : "—");
        rows[5] = new("int32 BE",      b.Length >= 4 ? BinaryPrimitives.ReadInt32BigEndian(b).ToString(CultureInfo.InvariantCulture) : "—");
        rows[6] = new("int64 LE",      b.Length >= 8 ? BinaryPrimitives.ReadInt64LittleEndian(b).ToString(CultureInfo.InvariantCulture) : "—");
        rows[7] = new("float32 LE",    b.Length >= 4 ? BinaryPrimitives.ReadSingleLittleEndian(b).ToString("G7", CultureInfo.InvariantCulture) : "—");
        rows[8] = new("float64 LE",    b.Length >= 8 ? BinaryPrimitives.ReadDoubleLittleEndian(b).ToString("G17", CultureInfo.InvariantCulture) : "—");
        rows[9] = new("GUID",          b.Length >= 16 ? new Guid(b[..16]).ToString("D") : "—");
        rows[10] = new("Unix epoch",   b.Length >= 8 ? FormatUnixEpoch(BinaryPrimitives.ReadInt64LittleEndian(b)) : "—");
        rows[11] = new("FILETIME",     b.Length >= 8 ? FormatFiletime(BinaryPrimitives.ReadInt64LittleEndian(b)) : "—");
        return rows;
    }

    private static string FormatUnixEpoch(long seconds)
    {
        if (seconds is < -62135596800 or > 253402300799)
        {
            return "out of range";
        }
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("u", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "out of range";
        }
    }

    private static string FormatFiletime(long ft)
    {
        // Windows FILETIME: 100-ns intervals since 1601-01-01 UTC.
        if (ft <= 0 || ft > 2650467744000000000L) // ~year 9999
        {
            return "out of range";
        }
        try
        {
            return DateTime.FromFileTimeUtc(ft).ToString("u", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "out of range";
        }
    }
}

public sealed record InspectorRow(string Label, string Value);
