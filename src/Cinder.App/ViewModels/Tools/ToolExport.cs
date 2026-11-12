using Cinder.App.Services;
using Cinder.Core.Custody;
using Cinder.Core.Export;

namespace Cinder.App.ViewModels.Tools;

/// <summary>
/// One export path for every tool that shows a table: ask where to save, write CSV or JSON
/// through <see cref="TabularExporter"/> (which carries the spreadsheet formula guard), and
/// record the export in the custody log when a case is open. Returns the line to show in the
/// tool's status, or null when the examiner cancelled the save dialog.
/// </summary>
internal static class ToolExport
{
    public static async Task<string?> SaveRowsAsync(
        string toolId, string baseName, string format, IReadOnlyList<object> rows, CancellationToken ct)
    {
        // Log to the case this work started under, even if the examiner switches case before it ends.
        var custodyCase = ActiveCaseContext.Current;
        if (rows.Count == 0)
        {
            return "Nothing to export yet.";
        }
        var ext = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "csv";
        var path = await ToolDialog.SaveFileAsync($"Export as {ext.ToUpperInvariant()}", $"{baseName}.{ext}", ext);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        try
        {
            var n = await Task.Run(() =>
            {
                if (ext == "json")
                {
                    using var fs = File.Create(path);
                    return TabularExporter.WriteJson(rows, fs);
                }
                using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return TabularExporter.WriteCsv(rows, w);
            }, ct);
            await ActiveCaseContext.LogAsync(custodyCase, CustodyAction.DataExported, new { Tool = toolId, Format = ext, Path = path, Rows = n }, ct);
            return $"Exported {n:N0} row{(n == 1 ? "" : "s")} to {path}";
        }
        catch (OperationCanceledException)
        {
            return "Export cancelled.";
        }
        catch (Exception ex)
        {
            return $"Export failed: {ex.Message}";
        }
    }
}
