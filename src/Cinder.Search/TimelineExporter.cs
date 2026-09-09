using System.Globalization;
using System.Text.Json;
using Cinder.Core.Export;

namespace Cinder.Search;

/// <summary>
/// Writes timeline events in the interchange formats the rest of the DFIR ecosystem consumes.
///
/// <list type="bullet">
/// <item><b>Timesketch JSONL</b> — one JSON object per line with the three fields Timesketch
/// requires (<c>message</c>, <c>datetime</c>, <c>timestamp_desc</c>) plus source, user and tags.
/// Streams from disk on import, so it is the right choice for large timelines.</item>
/// <item><b>Timesketch CSV</b> — the same columns as CSV, for the upload dialog or a
/// spreadsheet.</item>
/// <item><b>Bodyfile (mactime)</b> — the Sleuth Kit's pipe-delimited body format, read by
/// <c>mactime</c>, Autopsy, Plaso and most timeline tooling. Each event's timestamp lands in
/// the M/A/C/B slot its source implies; the other slots are zero.</item>
/// </list>
/// </summary>
public static class TimelineExporter
{
    public static int WriteTimesketchJsonl(IEnumerable<TimelineEvent> events, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(writer);

        var count = 0;
        foreach (var e in events)
        {
            var line = JsonSerializer.Serialize(new
            {
                message = e.Summary,
                datetime = Iso(e.Timestamp),
                timestamp_desc = DescribeTimestamp(e.Source),
                source = e.Source,
                user = e.User ?? "",
                tag = e.Tags,
            });
            writer.WriteLine(line);
            count++;
        }
        return count;
    }

    public static int WriteTimesketchCsv(IEnumerable<TimelineEvent> events, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine("message,datetime,timestamp_desc,source,user,tag");
        var count = 0;
        foreach (var e in events)
        {
            writer.WriteLine(string.Join(",",
                TabularExporter.CsvField(e.Summary),
                Iso(e.Timestamp),
                TabularExporter.CsvField(DescribeTimestamp(e.Source)),
                TabularExporter.CsvField(e.Source),
                TabularExporter.CsvField(e.User),
                TabularExporter.CsvField(string.Join(" ", e.Tags))));
            count++;
        }
        return count;
    }

    /// <summary>
    /// Sleuth Kit body format: <c>MD5|name|inode|mode|UID|GID|size|atime|mtime|ctime|crtime</c>,
    /// times as Unix epoch seconds. Pipes inside the name are replaced so the record stays
    /// eleven fields wide.
    /// </summary>
    public static int WriteBodyfile(IEnumerable<TimelineEvent> events, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(writer);

        var count = 0;
        foreach (var e in events)
        {
            var epoch = e.Timestamp.ToUnixTimeSeconds();
            var slot = MacbSlot(e.Source);
            var atime = slot == 'A' ? epoch : 0;
            var mtime = slot == 'M' ? epoch : 0;
            var ctime = slot == 'C' ? epoch : 0;
            var crtime = slot == 'B' ? epoch : 0;

            var name = $"[{e.Source}] {e.Summary}".Replace('|', '¦').Replace('\n', ' ').Replace('\r', ' ');
            writer.WriteLine(string.Join("|",
                "0", name, "0", "", "0", "0", "0",
                atime.ToString(CultureInfo.InvariantCulture),
                mtime.ToString(CultureInfo.InvariantCulture),
                ctime.ToString(CultureInfo.InvariantCulture),
                crtime.ToString(CultureInfo.InvariantCulture)));
            count++;
        }
        return count;
    }

    /// <summary>
    /// Timesketch's <c>timestamp_desc</c> says what the timestamp represents. It is derived
    /// from the source, because Cinder's sources already encode it: <c>lnk.created</c> is a
    /// creation time, <c>prefetch</c> is a last-run time, and so on.
    /// </summary>
    public static string DescribeTimestamp(string source)
    {
        if (source.StartsWith("evtx", StringComparison.OrdinalIgnoreCase)) return "Event Time";
        return source.ToLowerInvariant() switch
        {
            "prefetch" => "Last Run Time",
            "registry.userassist" => "Last Executed Time",
            "browser.history" => "Last Visited Time",
            "lnk.created" => "Creation Time",
            "lnk.modified" => "Content Modification Time",
            "lnk.accessed" => "Last Access Time",
            "email" => "Sent Time",
            "recyclebin" => "Deletion Time",
            "memory.process" => "Process Start Time",
            _ => "Event Time",
        };
    }

    /// <summary>Which of the four bodyfile slots (M/A/C/B) the source's timestamp belongs in.</summary>
    public static char MacbSlot(string source) => source.ToLowerInvariant() switch
    {
        "lnk.created" => 'B',
        "lnk.accessed" => 'A',
        "lnk.modified" => 'M',
        "recyclebin" => 'C',
        _ => 'M',
    };

    private static string Iso(DateTimeOffset ts) =>
        ts.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}
