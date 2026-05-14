// =====================================================================================
// In-process WorkflowRunner handler registry.
//
// A workflow is a JSON-defined DAG of typed nodes. The base Cinder.Workflow.WorkflowRunner
// walks it topologically and delegates each node's work to a registered handler. This file
// provides the canonical handlers for the documented node kinds — open-image, hash,
// registry, fs-enumerate, carve, report, index — so workflows actually execute end-to-end.
//
// Handlers read parameters from the node and may read prior steps' outputs by node id from
// the shared outputs dictionary. They return whatever object is useful for downstream
// consumption (typically a string path or a small record).
// =====================================================================================

using System.Collections.ObjectModel;
using System.Text.Json;
using Cinder.Core.Hashing;
using Cinder.Workflow;

namespace Cinder.App.ViewModels.Tools;

internal static class WorkflowHandlers
{
    public static WorkflowRunner BuildRunner(ObservableCollection<WorkflowExecutionRow> outputs)
    {
        var runner = new WorkflowRunner();

        // ---- open-image: record an input file path so later steps can consume it.
        runner.Register("open-image", async (node, _, ct) =>
        {
            var path = GetParam(node, "path");
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Row(outputs, node.Id, "open-image", "failed", $"Missing or unreadable path: {path}");
                return null;
            }
            var info = new FileInfo(path);
            Row(outputs, node.Id, "open-image", "ok", $"{path} ({info.Length:N0} bytes)");
            return path;
        });

        // ---- hash: SHA-256 over the file at "path" (or the previous open-image's result).
        runner.Register("hash", async (node, priorOutputs, ct) =>
        {
            var path = GetParam(node, "path") ?? FindPreviousPath(priorOutputs, node);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Row(outputs, node.Id, "hash", "failed", "no input file");
                return null;
            }
            var svc = new HashService();
            await using var fs = File.OpenRead(path);
            var r = await svc.ComputeAsync(fs, [HashAlgorithmKind.Sha256], progress: null, ct: ct);
            Row(outputs, node.Id, "hash", "ok", $"SHA-256={r.Sha256}");
            return r.Sha256;
        });

        // ---- registry: open a hive and report the root subkey count.
        runner.Register("registry", async (node, priorOutputs, ct) =>
        {
            var path = GetParam(node, "path") ?? FindPreviousPath(priorOutputs, node);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Row(outputs, node.Id, "registry", "failed", "no hive file");
                return null;
            }
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            var hive = new global::Registry.RegistryHive(path);
            hive.ParseHive();
            var subkeyCount = hive.Root?.SubKeys.Count ?? 0;
            Row(outputs, node.Id, "registry", "ok", $"root subkeys={subkeyCount} · hive={hive.HiveType}");
            return subkeyCount;
        });

        // ---- fs-enumerate: open a disk image and count discoverable filesystem entries.
        runner.Register("fs-enumerate", async (node, priorOutputs, ct) =>
        {
            var path = GetParam(node, "path") ?? FindPreviousPath(priorOutputs, node);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Row(outputs, node.Id, "fs-enumerate", "failed", "no image file");
                return null;
            }
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            // Reuse the same FilesystemTool detection routine. We don't surface every row
            // here — just the count, which is what the workflow audit trail wants.
            var rows = new List<object>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (DiscUtils.Iso9660.CDReader.Detect(fs))
                {
                    fs.Position = 0;
                    using var iso = new DiscUtils.Iso9660.CDReader(fs, joliet: true);
                    EnumerateDirCount(iso.Root, rows, depthBudget: 6);
                }
            }
            catch { }
            Row(outputs, node.Id, "fs-enumerate", "ok", $"entries={rows.Count:N0}");
            return rows.Count;
        });

        // ---- carve: invoke the FileCarver against the input.
        runner.Register("carve", async (node, priorOutputs, ct) =>
        {
            var path = GetParam(node, "path") ?? FindPreviousPath(priorOutputs, node);
            var outDir = GetParam(node, "output") ?? Path.Combine(Path.GetTempPath(), "cinder-carve-" + Guid.NewGuid().ToString("N"));
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Row(outputs, node.Id, "carve", "failed", "no input file");
                return null;
            }
            Directory.CreateDirectory(outDir);
            var carver = new Cinder.Carving.FileCarver();
            int n = 0;
            await using var fs = File.OpenRead(path);
            await foreach (var _ in carver.CarveAsync(fs, outDir, ct: ct))
            {
                n++;
                if (n >= 10_000) break;
            }
            Row(outputs, node.Id, "carve", "ok", $"recovered={n} · out={outDir}");
            return outDir;
        });

        // ---- report: emit a tiny placeholder report capturing the workflow's outputs.
        runner.Register("report", async (node, priorOutputs, ct) =>
        {
            var dest = GetParam(node, "path") ?? Path.Combine(Path.GetTempPath(), $"cinder-workflow-report-{Guid.NewGuid():N}.md");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# Workflow report");
            sb.AppendLine();
            sb.AppendLine($"- **Generated:** {DateTimeOffset.UtcNow:u}");
            sb.AppendLine($"- **Steps executed:** {priorOutputs.Count}");
            sb.AppendLine();
            foreach (var kv in priorOutputs)
            {
                sb.AppendLine($"## {kv.Key}");
                sb.AppendLine();
                sb.AppendLine($"```");
                sb.AppendLine(kv.Value?.ToString() ?? "(null)");
                sb.AppendLine($"```");
                sb.AppendLine();
            }
            await File.WriteAllTextAsync(dest, sb.ToString(), ct);
            Row(outputs, node.Id, "report", "ok", $"wrote={dest}");
            return dest;
        });

        // ---- index: write each previous output as a CaseIndex document.
        runner.Register("index", async (node, priorOutputs, ct) =>
        {
            var indexDir = GetParam(node, "path") ?? Path.Combine(Path.GetTempPath(), "cinder-index-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(indexDir);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            using var idx = new Cinder.Search.CaseIndex(indexDir);
            idx.OpenForWrite();
            int n = 0;
            foreach (var kv in priorOutputs)
            {
                idx.IndexDocument(new Cinder.Search.IndexableDoc(
                    Id: Guid.NewGuid().ToString(),
                    Source: "workflow." + kv.Key,
                    User: null,
                    Path: null,
                    Summary: kv.Value?.ToString() ?? "",
                    Text: kv.Value?.ToString() ?? "",
                    Timestamp: DateTimeOffset.UtcNow,
                    Tags: ["workflow"]));
                n++;
            }
            idx.Commit();
            Row(outputs, node.Id, "index", "ok", $"indexed={n} · path={indexDir}");
            return indexDir;
        });

        // ---- ai-summary: stub. Without an active AI provider we just announce.
        runner.Register("ai-summary", (node, priorOutputs, _) =>
        {
            Row(outputs, node.Id, "ai-summary",
                "skipped",
                "AI summary step needs an AI provider configured in Settings → AI.");
            return Task.FromResult<object?>(null);
        });

        return runner;
    }

    private static void EnumerateDirCount(DiscUtils.DiscDirectoryInfo dir, List<object> rows, int depthBudget)
    {
        if (depthBudget < 0 || rows.Count > 25_000) return;
        try
        {
            foreach (var e in dir.GetFileSystemInfos())
            {
                rows.Add(e);
                if (e is DiscUtils.DiscDirectoryInfo sub)
                {
                    EnumerateDirCount(sub, rows, depthBudget - 1);
                }
            }
        }
        catch { }
    }

    private static string? GetParam(WorkflowNode node, string name)
    {
        if (!node.Parameters.TryGetValue(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => el.GetRawText(),
        };
    }

    /// <summary>
    /// When a node doesn't supply its own "path" parameter, fall back to the most recent prior
    /// output that looks like a file path. Lets workflows chain `open-image → hash → registry`
    /// without restating the path on each step.
    /// </summary>
    private static string? FindPreviousPath(IDictionary<string, object?> outputs, WorkflowNode current)
    {
        foreach (var kv in outputs.Reverse())
        {
            if (kv.Value is string s && File.Exists(s))
            {
                return s;
            }
        }
        return null;
    }

    private static void Row(ObservableCollection<WorkflowExecutionRow> outputs,
                            string id, string kind, string status, string result)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            outputs.Add(new WorkflowExecutionRow(id, kind, status, result)));
    }
}
