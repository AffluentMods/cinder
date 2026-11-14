using System.Text;
using System.Text.Json;
using Avalonia.Data.Converters;

namespace Cinder.App.Services;

/// <summary>
/// Turns a custody entry's stored action id and JSON details into words for the table.
/// Display only: the entry itself, its hash and every export keep the stored text exactly.
/// </summary>
public static class CustodyText
{
    /// <summary>"case.opened" becomes "Case opened"; an id this build does not know is shown as stored.</summary>
    public static string Action(string? action) => action switch
    {
        "case.created" => "Case created",
        "case.opened" => "Case opened",
        "evidence.hashed" => "Evidence hashed",
        "evidence.imaged" => "Evidence imaged",
        "evidence.mounted" => "Evidence mounted",
        "evidence.unmounted" => "Evidence unmounted",
        "parser.ran" => "Tool run",
        "report.exported" => "Report exported",
        "data.exported" => "Data exported",
        "annotation" => "Note",
        null => "",
        _ => action,
    };

    /// <summary>
    /// A flat JSON object becomes "Name: value  ·  Name: value". Empty values are left out.
    /// Anything that is not a JSON object is returned unchanged.
    /// </summary>
    public static string Details(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "";
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return json;
            }
            var sb = new StringBuilder();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.True => "yes",
                    JsonValueKind.False => "no",
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => property.Value.GetRawText(),
                };
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }
                if (sb.Length > 0)
                {
                    sb.Append("  ·  ");
                }
                sb.Append(Label(property.Name)).Append(": ").Append(value);
            }
            return sb.Length == 0 ? json : sb.ToString();
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>"BytesWritten" becomes "Bytes written"; "MD5" and "Sha256" keep their shape.</summary>
    private static string Label(string name)
    {
        if (name.Length < 2 || name.All(c => char.IsUpper(c) || char.IsDigit(c)))
        {
            return name;
        }
        var sb = new StringBuilder(name.Length + 4);
        sb.Append(char.ToUpperInvariant(name[0]));
        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && char.IsLower(name[i - 1]))
            {
                sb.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    public static IValueConverter ActionConverter { get; } =
        new FuncValueConverter<string?, string>(Action);

    public static IValueConverter DetailsConverter { get; } =
        new FuncValueConverter<string?, string>(Details);
}
