using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Cinder.Core.Export;

/// <summary>
/// Writes a sequence of same-shaped row objects (the anonymous records every parser tool
/// produces) as CSV or JSON. Columns come from the public readable properties of the first
/// row, in declaration order, so a grid exports exactly what it displayed.
///
/// <para><b>CSV formula injection.</b> Evidence contains hostile strings by definition, and
/// a filename or registry value beginning with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or
/// CR is executed as a formula by Excel and LibreOffice when the export is opened. String cells
/// starting with one of those characters are prefixed with an apostrophe, which spreadsheets
/// treat as "literal text". Numeric cells are written raw, so a negative number stays a number.</para>
/// </summary>
public static class TabularExporter
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ColumnCache = new();

    /// <summary>Writes RFC 4180 CSV with a header row. Returns the number of data rows written.</summary>
    public static int WriteCsv(IEnumerable<object> rows, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(writer);

        PropertyInfo[]? columns = null;
        var count = 0;
        foreach (var row in rows)
        {
            if (row is null)
            {
                continue;
            }
            if (columns is null)
            {
                columns = ColumnsOf(row.GetType());
                writer.WriteLine(string.Join(",", columns.Select(c => CsvField(c.Name))));
            }

            var cells = new string[columns.Length];
            for (int i = 0; i < columns.Length; i++)
            {
                cells[i] = CsvCell(columns[i].GetValue(row));
            }
            writer.WriteLine(string.Join(",", cells));
            count++;
        }
        return count;
    }

    /// <summary>Writes an indented JSON array of objects. Returns the number of rows written.</summary>
    public static int WriteJson(IEnumerable<object> rows, Stream output)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(output);

        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartArray();

        PropertyInfo[]? columns = null;
        var count = 0;
        foreach (var row in rows)
        {
            if (row is null)
            {
                continue;
            }
            columns ??= ColumnsOf(row.GetType());

            json.WriteStartObject();
            foreach (var c in columns)
            {
                json.WritePropertyName(c.Name);
                WriteJsonValue(json, c.GetValue(row));
            }
            json.WriteEndObject();
            count++;
        }

        json.WriteEndArray();
        json.Flush();
        return count;
    }

    /// <summary>
    /// Quotes a single CSV field per RFC 4180 and applies the formula-injection guard. Exposed
    /// so other exporters (the timeline's Timesketch CSV, for one) escape identically.
    /// </summary>
    public static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var guarded = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + value
            : value;

        if (guarded.IndexOfAny(['"', ',', '\n', '\r']) < 0)
        {
            return guarded;
        }
        return "\"" + guarded.Replace("\"", "\"\"") + "\"";
    }

    private static string CsvCell(object? value) => value switch
    {
        null => "",
        string s => CsvField(s),
        bool b => b ? "true" : "false",
        DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => CsvField(value.ToString()),
    };

    private static void WriteJsonValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null: json.WriteNullValue(); break;
            case string s: json.WriteStringValue(s); break;
            case bool b: json.WriteBooleanValue(b); break;
            case int i: json.WriteNumberValue(i); break;
            case long l: json.WriteNumberValue(l); break;
            case uint ui: json.WriteNumberValue(ui); break;
            case ulong ul: json.WriteNumberValue(ul); break;
            case short sh: json.WriteNumberValue(sh); break;
            case ushort ush: json.WriteNumberValue(ush); break;
            case byte by: json.WriteNumberValue(by); break;
            case double d: json.WriteNumberValue(d); break;
            case float f: json.WriteNumberValue(f); break;
            case decimal m: json.WriteNumberValue(m); break;
            case DateTimeOffset dto: json.WriteStringValue(dto.ToUniversalTime()); break;
            case DateTime dt: json.WriteStringValue(dt.ToUniversalTime()); break;
            case Guid g: json.WriteStringValue(g); break;
            default: json.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    private static PropertyInfo[] ColumnsOf(Type t) => ColumnCache.GetOrAdd(t, static type =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray());

    /// <summary>
    /// One row as an ordered name → display-string map. This is what a bookmark stores, so the
    /// finding survives the source grid's columns changing in a later version.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ToDictionary(object row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var d = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var c in ColumnsOf(row.GetType()))
        {
            var v = c.GetValue(row);
            d[c.Name] = v switch
            {
                null => null,
                string s => s,
                DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString(),
            };
        }
        return d;
    }

    /// <summary>Convenience: serialise rows to a CSV string.</summary>
    public static string ToCsv(IEnumerable<object> rows)
    {
        var sb = new StringBuilder();
        using var w = new StringWriter(sb, CultureInfo.InvariantCulture);
        WriteCsv(rows, w);
        return sb.ToString();
    }
}
