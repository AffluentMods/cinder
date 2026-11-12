using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cinder.Sidecar;

/// <summary>
/// A time as a sidecar reported it.
/// </summary>
/// <param name="Value">
/// When <paramref name="ZoneKnown"/> is true, the instant, in UTC. When it is false the source
/// recorded a wall-clock reading and no zone (classic syslog, apt's history, a Trash info file):
/// <paramref name="Value"/> then holds that reading exactly as written with a placeholder
/// offset of +00:00. It is not a UTC instant and must not be shown or compared as one.
/// </param>
/// <param name="ZoneKnown">Whether <paramref name="Value"/> is a real instant.</param>
public readonly record struct SidecarTime(DateTimeOffset Value, bool ZoneKnown);

/// <summary>
/// Reads values out of a sidecar reply without trusting its shape.
/// <para>
/// A reply is built from evidence, and evidence is hostile: a field that is "always a string"
/// is a number in the one record that matters, a count does not fit in 32 bits, a timestamp is
/// the year 30000. <c>node.GetValue&lt;T&gt;()</c> throws on every one of those and takes the
/// whole listing with it. These readers return null instead, so one bad field costs one field.
/// </para>
/// <para>
/// They also never substitute a default. A field the worker sent as null (because it could not
/// determine the value) comes back as null; turning that into 0, 1 or year 0001 is the caller
/// inventing evidence, and is the caller's decision to justify.
/// </para>
/// </summary>
public static partial class SidecarJson
{
    /// <summary>
    /// Extras key marking how an artifact's timestamps are to be read. Same key and value as
    /// the filesystem walker uses for FAT volumes, so one check covers both.
    /// </summary>
    public const string TimeBasisKey = "time_basis";

    /// <summary><see cref="TimeBasisKey"/> value: local wall-clock time, zone not recorded.</summary>
    public const string TimeBasisLocalUnknownZone = "local-unknown-zone";

    [GeneratedRegex(@"(?:[zZ]|[+\-]\d{2}:?\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoneSuffix();

    /// <summary>A string value; a number or boolean as its JSON text; null for anything else.</summary>
    public static string? String(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                return value.TryGetValue<string>(out var text) ? text : value.ToString();
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return value.ToJsonString();
            default:
                return null;
        }
    }

    /// <summary>An integer that fits in 64 bits (from a number, or a string of digits); else null.</summary>
    public static long? Int64(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        string text;
        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                text = value.ToJsonString();
                break;
            case JsonValueKind.String:
                text = (String(value) ?? "").Trim();
                break;
            default:
                return null;
        }
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exact))
        {
            return exact;
        }
        // 12.0 and 1.2e1 are integers too; 12.5, NaN and 1e30 are not.
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && double.IsFinite(d) && Math.Floor(d) == d && d >= -9.2233720368547758E+18 && d < 9.2233720368547758E+18)
        {
            return (long)d;
        }
        return null;
    }

    /// <summary>An integer that fits in 32 bits; else null (never a truncated value).</summary>
    public static int? Int32(JsonNode? node)
        => Int64(node) is { } v && v >= int.MinValue && v <= int.MaxValue ? (int)v : null;

    /// <summary>A finite number; else null.</summary>
    public static double? Double(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }
        return double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
               && double.IsFinite(d) ? d : null;
    }

    /// <summary>A JSON boolean; else null.</summary>
    public static bool? Boolean(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>The object elements of an array; empty when the node is not an array.</summary>
    public static IEnumerable<JsonObject> Objects(JsonNode? node)
        => node is JsonArray array ? array.OfType<JsonObject>() : [];

    /// <summary>The string elements of an array (see <see cref="String"/>); empty when not an array.</summary>
    public static IReadOnlyList<string> Strings(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }
        var list = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (String(item) is { } text)
            {
                list.Add(text);
            }
        }
        return list;
    }

    /// <summary>
    /// A timestamp string as a <see cref="SidecarTime"/>; null when the field is null, empty or
    /// not a representable time. Null stays null: there is no "minimum date" fallback.
    /// </summary>
    /// <param name="node">The field.</param>
    /// <param name="utcByDefinition">
    /// Pass true only for a source whose format defines the value as UTC even when the text
    /// carries no offset (an event log's <c>SystemTime</c>). Otherwise text without an offset
    /// is a wall-clock reading in an unknown zone and is reported as such.
    /// </param>
    public static SidecarTime? Time(JsonNode? node, bool utcByDefinition = false)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }
        var text = (String(value) ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }
        if (utcByDefinition || ZoneSuffix().IsMatch(text))
        {
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
                ? new SidecarTime(instant, ZoneKnown: true)
                : null;
        }
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wallClock)
            ? new SidecarTime(new DateTimeOffset(DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified), TimeSpan.Zero), ZoneKnown: false)
            : null;
    }

    /// <summary>
    /// <see cref="Time"/> for a field that is an instant by construction (the worker derived it
    /// from an epoch or a FILETIME and always writes an offset). A value without an offset is
    /// still returned, as the reading it is; use <see cref="Time"/> when the zone can be unknown.
    /// </summary>
    public static DateTimeOffset? Instant(JsonNode? node, bool utcByDefinition = false)
        => Time(node, utcByDefinition)?.Value;
}
