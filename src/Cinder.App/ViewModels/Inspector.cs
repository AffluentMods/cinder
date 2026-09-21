using System.Buffers.Binary;
using System.Globalization;

namespace Cinder.App.ViewModels;

/// <summary>
/// Decodes the bytes at the caret as the integer, float, identifier and timestamp encodings an
/// examiner meets in Windows, macOS and browser artefacts, in the chosen byte order.
///
/// <para>Every timestamp carries a plausibility note. Eight arbitrary bytes will always decode
/// to <em>some</em> date; flagging one that lands in the future, or long before the format
/// existed, is what stops a coincidence being read as evidence.</para>
/// </summary>
public static class Inspector
{
    public static IReadOnlyList<InspectorRow> Decode(ReadOnlySpan<byte> b, bool littleEndian, DateTimeOffset now)
    {
        var rows = new List<InspectorRow>(24);
        var inv = CultureInfo.InvariantCulture;

        rows.Add(Row("int8", 1, b, s => ((sbyte)s[0]).ToString(inv)));
        rows.Add(Row("uint8", 1, b, s => s[0].ToString(inv)));
        rows.Add(Row("int16", 2, b, s => (littleEndian ? BinaryPrimitives.ReadInt16LittleEndian(s) : BinaryPrimitives.ReadInt16BigEndian(s)).ToString(inv)));
        rows.Add(Row("uint16", 2, b, s => (littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s)).ToString(inv)));
        rows.Add(Row("int32", 4, b, s => (littleEndian ? BinaryPrimitives.ReadInt32LittleEndian(s) : BinaryPrimitives.ReadInt32BigEndian(s)).ToString(inv)));
        rows.Add(Row("uint32", 4, b, s => (littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s)).ToString(inv)));
        rows.Add(Row("int64", 8, b, s => (littleEndian ? BinaryPrimitives.ReadInt64LittleEndian(s) : BinaryPrimitives.ReadInt64BigEndian(s)).ToString(inv)));
        rows.Add(Row("uint64", 8, b, s => (littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(s) : BinaryPrimitives.ReadUInt64BigEndian(s)).ToString(inv)));
        rows.Add(InspectorRow.Divider);

        rows.Add(Row("float32", 4, b, s => FormatFloat(littleEndian ? BinaryPrimitives.ReadSingleLittleEndian(s) : BinaryPrimitives.ReadSingleBigEndian(s))));
        rows.Add(Row("float64", 8, b, s => FormatFloat(littleEndian ? BinaryPrimitives.ReadDoubleLittleEndian(s) : BinaryPrimitives.ReadDoubleBigEndian(s))));
        // .NET's Guid(ReadOnlySpan) is the Windows mixed-endian layout regardless of the toggle —
        // label it so nobody reads it as a big-endian RFC 4122 UUID.
        rows.Add(b.Length >= 16
            ? new InspectorRow("GUID", new Guid(b[..16]).ToString("D"), "mixed")
            : InspectorRow.NeedsBytes("GUID"));
        rows.Add(InspectorRow.Divider);

        // Timestamps.
        rows.Add(Time("Unix time", 4, b, s =>
        {
            var v = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
            return DateTimeOffset.FromUnixTimeSeconds(v);
        }, now, earliest: 1990));
        rows.Add(Time("FILETIME", 8, b, s =>
        {
            var v = littleEndian ? BinaryPrimitives.ReadInt64LittleEndian(s) : BinaryPrimitives.ReadInt64BigEndian(s);
            return v is <= 0 or > 2650467743999999999L ? null : new DateTimeOffset(DateTime.FromFileTimeUtc(v));
        }, now, earliest: 1995));
        rows.Add(Time("DOS date", 4, b, s =>
        {
            // FAT / ZIP: time word then date word, each in the file's byte order (always LE on disk).
            var time = littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
            var date = littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(s[2..]) : BinaryPrimitives.ReadUInt16BigEndian(s[2..]);
            int y = 1980 + (date >> 9), mo = (date >> 5) & 15, d = date & 31;
            int h = time >> 11, mi = (time >> 5) & 63, sec = (time & 31) * 2;
            if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || sec > 59)
            {
                return null;
            }
            return new DateTimeOffset(y, mo, d, h, mi, sec, TimeSpan.Zero);
        }, now, earliest: 1981));
        rows.Add(Time("WebKit time", 8, b, s =>
        {
            // Chrome / Edge / Safari history: microseconds since 1601-01-01 UTC.
            var v = littleEndian ? BinaryPrimitives.ReadInt64LittleEndian(s) : BinaryPrimitives.ReadInt64BigEndian(s);
            return v is <= 0 or > 265046774399999999L ? null : new DateTimeOffset(DateTime.FromFileTimeUtc(v * 10));
        }, now, earliest: 2005));
        rows.Add(Time("HFS+ time", 4, b, s =>
        {
            // macOS HFS+: seconds since 1904-01-01 UTC, stored big-endian on disk.
            var v = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
            return new DateTimeOffset(1904, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(v);
        }, now, earliest: 1998));
        rows.Add(Time("OLE date", 8, b, s =>
        {
            var v = littleEndian ? BinaryPrimitives.ReadDoubleLittleEndian(s) : BinaryPrimitives.ReadDoubleBigEndian(s);
            if (double.IsNaN(v) || v < -657435.0 || v > 2958465.99999999)
            {
                return null;
            }
            return new DateTimeOffset(DateTime.FromOADate(v), TimeSpan.Zero);
        }, now, earliest: 1990));

        return rows;
    }

    private static InspectorRow Row(string label, int need, ReadOnlySpan<byte> b, Func<ReadOnlySpan<byte>, string> decode) =>
        b.Length >= need ? new InspectorRow(label, decode(b[..need])) : InspectorRow.NeedsBytes(label);

    private delegate DateTimeOffset? TimeDecoder(ReadOnlySpan<byte> s);

    private static InspectorRow Time(string label, int need, ReadOnlySpan<byte> b, TimeDecoder decode, DateTimeOffset now, int earliest)
    {
        if (b.Length < need)
        {
            return InspectorRow.NeedsBytes(label);
        }
        DateTimeOffset? t;
        try
        {
            t = decode(b[..need]);
        }
        catch (ArgumentOutOfRangeException)
        {
            t = null;
        }
        if (t is not { } v)
        {
            return new InspectorRow(label, "out of range", null, IsDim: true);
        }
        var text = v.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (v > now.AddDays(1))
        {
            return new InspectorRow(label, text, "future", IsDim: true);
        }
        if (v.Year < earliest)
        {
            return new InspectorRow(label, text, "unlikely", IsDim: true);
        }
        return new InspectorRow(label, text);
    }

    private static string FormatFloat(double v)
    {
        if (!double.IsFinite(v))
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }
        var a = Math.Abs(v);
        return a != 0 && (a >= 1e7 || a < 1e-4)
            ? v.ToString("0.####e+0", CultureInfo.InvariantCulture)
            : v.ToString("G7", CultureInfo.InvariantCulture);
    }
}

/// <summary>One decoded value. <see cref="Note"/> is a short qualifier ("UTC", "future", "mixed").</summary>
public sealed record InspectorRow(string Label, string Value, string? Note = null, bool IsDim = false, bool IsDivider = false)
{
    public bool HasNote => !string.IsNullOrEmpty(Note);

    public static readonly InspectorRow Divider = new("", "", IsDivider: true);

    public static InspectorRow NeedsBytes(string label) => new(label, "needs more bytes", null, IsDim: true);
}
