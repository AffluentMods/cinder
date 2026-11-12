// =====================================================================================
// The steps a workflow file can contain.
//
// A workflow is a JSON list of typed steps and the order they depend on each other.
// Cinder.Workflow.WorkflowRunner walks it and hands each step to the handler registered here
// for its kind: open-image, hash, registry, fs-enumerate, carve, report, index, ai-summary.
//
// A step that cannot do its work THROWS. It does not report "failed" and return, because the
// runner would then carry on, later steps would run against nothing, and the run would end
// "Done". The exception names the step, so the examiner sees which one stopped the run and
// why. A step reports a row only for work it actually did.
// =====================================================================================

using System.Collections.ObjectModel;
using System.Text.Json;
using Cinder.Core.Hashing;
using Cinder.Filesystems;
using Cinder.Imaging;
using Cinder.Workflow;

namespace Cinder.App.ViewModels.Tools;

internal static class WorkflowHandlers
{
    /// <summary>Most files one carve step recovers. Reaching it is stated in the step's result.</summary>
    private const int MaxCarved = 10_000;

    /// <summary>The runner the Workflows tool uses: results are added to its list on the UI thread.</summary>
    public static WorkflowRunner BuildRunner(ObservableCollection<WorkflowExecutionRow> outputs) =>
        BuildRunner(row => Avalonia.Threading.Dispatcher.UIThread.Post(() => outputs.Add(row)));

    /// <summary>Builds a runner that passes each completed step's result to <paramref name="report"/>.</summary>
    public static WorkflowRunner BuildRunner(Action<WorkflowExecutionRow> report)
    {
        var runner = new WorkflowRunner();

        void Ok(WorkflowNode node, string result) => report(new WorkflowExecutionRow(node.Id, node.Kind, "ok", result));

        // ---- open-image: check an input file exists so later steps can use it.
        runner.Register("open-image", async (node, _, ct) =>
        {
            var path = GetParam(node, "path");
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(path))
            {
                throw Fail(node, "it has no \"path\" setting");
            }
            if (!File.Exists(path))
            {
                throw Fail(node, $"the file does not exist: {path}");
            }
            Ok(node, $"Found {path} ({new FileInfo(path).Length:N0} bytes).");
            return path;
        });

        // ---- hash: SHA-256 of the file at "path", or of the file an earlier step produced.
        runner.Register("hash", async (node, priorOutputs, ct) =>
        {
            var path = InputFile(node, priorOutputs);
            var svc = new HashService();
            await using var fs = File.OpenRead(path);
            var r = await svc.ComputeAsync(fs, [HashAlgorithmKind.Sha256], progress: null, ct: ct);
            Ok(node, $"SHA-256 of {Path.GetFileName(path)}: {r.Sha256}");
            return r.Sha256;
        });

        // ---- registry: open a hive and report how many keys sit under its root.
        runner.Register("registry", async (node, priorOutputs, ct) =>
        {
            var path = InputFile(node, priorOutputs);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            global::Registry.RegistryHive hive;
            try
            {
                hive = HiveReader.Open(path);
            }
            catch (InvalidOperationException ex)
            {
                throw Fail(node, ex.Message);
            }
            var subkeyCount = hive.Root?.SubKeys.Count ?? 0;
            Ok(node, $"Opened a {hive.HiveType} registry hive with {subkeyCount:N0} key{(subkeyCount == 1 ? "" : "s")} under its root.");
            return subkeyCount;
        });

        // ---- fs-enumerate: count the files and folders in a disk image.
        runner.Register("fs-enumerate", async (node, priorOutputs, ct) =>
        {
            var path = InputFile(node, priorOutputs);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            WalkResult result;
            try
            {
                result = WalkImage(path, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Fail(node, $"the image could not be read: {ex.Message}");
            }
            if (result.Entries.Count == 0)
            {
                // Zero entries from a disk image means no filesystem was recognised. That is a
                // failure to enumerate, not an empty disk, and must not be reported as "ok".
                throw Fail(node, "no NTFS, FAT, ext or ISO 9660 filesystem was found in the image");
            }
            Ok(node, $"Listed {result.Entries.Count:N0} files and folders." + (result.Truncated ? " The listing stopped at its limit; the image holds more." : ""));
            return result.Entries.Count;
        });

        // ---- carve: recover files by signature from the input.
        runner.Register("carve", async (node, priorOutputs, ct) =>
        {
            var path = InputFile(node, priorOutputs);
            var outDir = GetParam(node, "output") ?? Path.Combine(Path.GetTempPath(), "cinder-carve-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            var carver = new Cinder.Carving.FileCarver();
            int n = 0;
            var capped = false;
            await using var fs = File.OpenRead(path);
            await foreach (var _ in carver.CarveAsync(fs, outDir, ct: ct))
            {
                if (++n >= MaxCarved)
                {
                    capped = true;
                    break;
                }
            }
            Ok(node, $"Recovered {n:N0} file{(n == 1 ? "" : "s")} into {outDir}." + (capped ? " Carving stopped at its limit; the input may hold more." : ""));
            return outDir;
        });

        // ---- report: write a short Markdown file listing what each earlier step returned.
        runner.Register("report", async (node, priorOutputs, ct) =>
        {
            var dest = GetParam(node, "path") ?? Path.Combine(Path.GetTempPath(), $"cinder-workflow-report-{Guid.NewGuid():N}.md");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Workflow report");
            sb.AppendLine();
            sb.AppendLine($"- **Generated:** {DateTimeOffset.UtcNow:u}");
            sb.AppendLine($"- **Steps executed:** {priorOutputs.Count}");
            sb.AppendLine();
            foreach (var kv in priorOutputs)
            {
                sb.AppendLine($"## {kv.Key}");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(kv.Value?.ToString() ?? "(nothing returned)");
                sb.AppendLine("```");
                sb.AppendLine();
            }
            await File.WriteAllTextAsync(dest, sb.ToString(), ct);
            Ok(node, $"Wrote the summary to {dest}.");
            return dest;
        });

        // ---- index: add each earlier step's result to a search index.
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
            Ok(node, $"Added {n:N0} result{(n == 1 ? "" : "s")} to the search index in {indexDir}.");
            return indexDir;
        });

        // ---- ai-summary: not implemented. Said plainly, and reported as skipped rather than ok.
        runner.Register("ai-summary", (node, _, _) =>
        {
            report(new WorkflowExecutionRow(node.Id, node.Kind, "skipped",
                "The AI summary step is not available yet, so nothing was summarised."));
            return Task.FromResult<object?>(null);
        });

        return runner;
    }

    /// <summary>The exception that stops a run: it names the step so the examiner knows which one and why.</summary>
    private static InvalidOperationException Fail(WorkflowNode node, string reason) =>
        new($"Step \"{node.Id}\" ({node.Kind}) stopped the workflow: {reason}.");

    /// <summary>
    /// The file a step works on: its own "path" setting, or the most recent earlier result that
    /// is an existing file. Throws, naming the step, when there is none.
    /// </summary>
    private static string InputFile(WorkflowNode node, IDictionary<string, object?> priorOutputs)
    {
        var own = GetParam(node, "path");
        if (!string.IsNullOrEmpty(own))
        {
            return File.Exists(own) ? own : throw Fail(node, $"the file does not exist: {own}");
        }
        foreach (var kv in priorOutputs.Reverse())
        {
            if (kv.Value is string s && File.Exists(s))
            {
                return s;
            }
        }
        throw Fail(node, "it has no \"path\" setting and no earlier step produced a file");
    }

    /// <summary>Lists a disk image the way the Filesystem tool does: raw, E01 and AFF4 through one opener, VHD and VHDX as disks.</summary>
    private static WalkResult WalkImage(string path, CancellationToken ct)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".vhd" or ".vhdx")
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using DiscUtils.VirtualDisk disk = ext == ".vhd"
                ? new DiscUtils.Vhd.Disk(file, DiscUtils.Streams.Ownership.None)
                : new DiscUtils.Vhdx.Disk(file, DiscUtils.Streams.Ownership.None);
            return DiscUtilsWalker.WalkDisk(disk, new WalkOptions(), ct);
        }
        using var stream = EvidenceOpener.Open(path);
        return DiscUtilsWalker.Walk(stream, new WalkOptions(), ct);
    }

    private static string? GetParam(WorkflowNode node, string name)
    {
        if (!node.Parameters.TryGetValue(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => el.GetRawText(),
        };
    }
}
