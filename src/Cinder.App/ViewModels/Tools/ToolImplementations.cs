using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Hashing;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Platform.Storage;
using Cinder.App.Services;
using Cinder.Cases;
using Cinder.Core.Cases;
using Cinder.Core.Custody;
using Cinder.Core.Hashing;
using Cinder.Imaging;
using Cinder.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels.Tools;

// =====================================================================================
// STRINGS — extract printable ASCII / UTF-16 strings from a file.
// =====================================================================================

public sealed partial class StringsTool
{
    public ObservableCollection<StringHit> Hits { get; } = new();

    [ObservableProperty] private string? _path;
    [ObservableProperty] private int _minLength = 6;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private async Task PickAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Pick a file to extract strings from");
        if (string.IsNullOrEmpty(path)) return;
        Path = path;
        await RunAsync(ct);
    }

    [RelayCommand]
    private async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Path)) return;
        Hits.Clear();
        IsLoading = true;
        StatusLine = "Scanning…";
        try
        {
            var minLen = Math.Max(3, MinLength);
            var hits = await Task.Run(() => Extract(Path, minLen, ct), ct);
            foreach (var h in hits)
            {
                Hits.Add(h);
                if (Hits.Count >= 50_000) break;
            }
            StatusLine = $"{Hits.Count:N0} string{(Hits.Count == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static IEnumerable<StringHit> Extract(string path, int minLen, CancellationToken ct)
    {
        var results = new List<StringHit>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        Span<byte> buf = stackalloc byte[1 << 16];
        long pos = 0;
        var asciiSb = new StringBuilder();
        long asciiStart = 0;
        var utf16Sb = new StringBuilder();
        long utf16Start = 0;
        bool utf16High = false;

        int read;
        while ((read = fs.Read(buf)) > 0 && results.Count < 50_000)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
            {
                var b = buf[i];
                // ASCII printable
                if (b is >= 0x20 and < 0x7F)
                {
                    if (asciiSb.Length == 0) asciiStart = pos + i;
                    asciiSb.Append((char)b);
                }
                else
                {
                    if (asciiSb.Length >= minLen)
                    {
                        results.Add(new StringHit(asciiStart, "ASCII", asciiSb.ToString()));
                    }
                    asciiSb.Clear();
                }

                // UTF-16LE: simple heuristic — printable ASCII followed by 0x00.
                if (utf16High)
                {
                    if (b == 0)
                    {
                        // valid 16-bit char already consumed
                    }
                    else
                    {
                        if (utf16Sb.Length >= minLen)
                        {
                            results.Add(new StringHit(utf16Start, "UTF-16LE", utf16Sb.ToString()));
                        }
                        utf16Sb.Clear();
                    }
                    utf16High = false;
                }
                else
                {
                    if (b is >= 0x20 and < 0x7F)
                    {
                        if (utf16Sb.Length == 0) utf16Start = pos + i;
                        utf16Sb.Append((char)b);
                        utf16High = true;
                    }
                    else
                    {
                        if (utf16Sb.Length >= minLen)
                        {
                            results.Add(new StringHit(utf16Start, "UTF-16LE", utf16Sb.ToString()));
                        }
                        utf16Sb.Clear();
                    }
                }
            }
            pos += read;
        }
        if (asciiSb.Length >= minLen) results.Add(new StringHit(asciiStart, "ASCII", asciiSb.ToString()));
        if (utf16Sb.Length >= minLen) results.Add(new StringHit(utf16Start, "UTF-16LE", utf16Sb.ToString()));
        return results;
    }
}

public sealed record StringHit(long Offset, string Encoding, string Value);

// =====================================================================================
// DOCUMENTS — pick + read text-ish files.
// =====================================================================================

public sealed partial class DocumentsTool
{
    [ObservableProperty] private string? _path;
    [ObservableProperty] private string? _content;
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private async Task PickAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Pick a document");
        if (string.IsNullOrEmpty(path)) return;
        Path = path;
        try
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            string text;
            if (ext == ".pdf")
            {
                text = "(PDF text extraction lands once PdfPig is added to the bundle. For now, drop the PDF in the hex viewer to inspect bytes.)";
            }
            else if (ext is ".rtf" or ".txt" or ".md" or ".log" or ".csv" or ".json" or ".xml" or ".html")
            {
                text = await File.ReadAllTextAsync(path, ct);
            }
            else
            {
                text = "(Cinder's document preview is text-only for v0.1. Open the file in the hex viewer for a full byte view.)";
            }
            Content = text;
            StatusLine = $"{new FileInfo(path).Length:N0} bytes";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
            Content = null;
        }
    }
}

// =====================================================================================
// CASES — workspace recents + create + open.
// =====================================================================================

public sealed partial class CasesTool
{
    private readonly Workspace _workspace = Workspace.LoadOrCreate();

    public ObservableCollection<WorkspaceCase> Recents { get; } = new();

    [ObservableProperty] private WorkspaceCase? _activeCase;
    [ObservableProperty] private string? _statusLine;

    public CasesTool()
    {
        Refresh();
    }

    private void Refresh()
    {
        Recents.Clear();
        foreach (var c in _workspace.RecentCases)
        {
            Recents.Add(c);
        }
        ActiveCase = _workspace.ActiveCaseId is { } id
            ? Recents.FirstOrDefault(c => c.Id == id)
            : null;
    }

    [RelayCommand]
    private async Task CreateAsync(CancellationToken ct)
    {
        var path = await ToolDialog.SaveFileAsync("Create case", "case.cinder", "cinder");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var store = new CaseStore(path);
            var custody = new CustodyLog(store);
            var svc = new CaseService(store, custody);
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var c = await svc.CreateAsync(name, Environment.UserName, null, ct);
            _workspace.RecordOpen(c.Id, path, name);
            _workspace.Save();
            Refresh();
            StatusLine = $"Created {name}.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Open case", "Cinder case", "*.cinder");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var store = new CaseStore(path);
            store.Migrate();
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            _workspace.RecordOpen(Guid.NewGuid(), path, name);
            _workspace.Save();
            Refresh();
            StatusLine = $"Opened {name}.";
            _ = ct;
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenRecent(WorkspaceCase? c)
    {
        if (c is null) return;
        ActiveCase = c;
        StatusLine = $"Active case: {c.DisplayName}";
    }
}

// =====================================================================================
// CUSTODY — open .cinder file, walk + verify chain.
// =====================================================================================

public sealed partial class CustodyTool
{
    [ObservableProperty] private string? _casePath;
    [ObservableProperty] private string? _verdict;
    [ObservableProperty] private string? _statusLine;

    public ObservableCollection<CustodyEntry> Entries { get; } = new();

    [RelayCommand]
    private async Task PickAndVerifyAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Open Cinder case", "Cinder case", "*.cinder");
        if (string.IsNullOrEmpty(path)) return;
        CasePath = path;
        Entries.Clear();
        try
        {
            var store = new CaseStore(path);
            store.Migrate();
            var log = new CustodyLog(store);
            // List & verify across every case in the file.
            using var conn = store.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT case_id FROM custody_entries;";
            using var r = cmd.ExecuteReader();
            var allOk = true;
            long total = 0;
            while (r.Read())
            {
                var caseId = Guid.Parse(r.GetString(0));
                var entries = await log.ListAsync(caseId, ct);
                foreach (var e in entries)
                {
                    Entries.Add(e);
                    total++;
                }
                var v = await log.VerifyAsync(caseId, ct);
                if (!v.Ok)
                {
                    allOk = false;
                    Verdict = $"⚠ chain broken at sequence {v.FirstBrokenSequence} ({v.Reason})";
                }
            }
            if (allOk) Verdict = $"✓ chain intact across {total:N0} entries";
            StatusLine = $"{Entries.Count:N0} entries shown.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
            Verdict = null;
        }
    }
}

// =====================================================================================
// HASH SETS — NSRL bulk import + manual lookup.
// =====================================================================================

public sealed partial class HashSetsTool
{
    private HashSetService? _service;

    [ObservableProperty] private string? _databasePath;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _algorithm = "sha1";
    [ObservableProperty] private string? _verdictLine;
    [ObservableProperty] private string? _statusLine;

    public IReadOnlyList<string> Algorithms { get; } = ["md5", "sha1", "sha256", "blake3"];

    [RelayCommand]
    private async Task PickDatabaseAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Pick or create the hash-set DB", "SQLite DB", "*.db");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            _service?.Dispose();
            _service = new HashSetService(path);
            DatabasePath = path;
            StatusLine = $"DB ready: {path}";
            _ = ct;
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportNsrlAsync(CancellationToken ct)
    {
        if (_service is null)
        {
            StatusLine = "Pick a DB first.";
            return;
        }
        var path = await ToolDialog.PickFileAsync("Pick NSRL minimal CSV", "NSRL minimal", "*.csv");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var n = await Task.Run(() => _service.ImportNsrlMinimalCsv(path, $"NSRL_{DateTime.UtcNow:yyyyMMdd}"), ct);
            StatusLine = $"Imported {n:N0} rows.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Lookup()
    {
        if (_service is null)
        {
            VerdictLine = "Pick a DB first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Query))
        {
            return;
        }
        try
        {
            var match = _service.Lookup(Algorithm, Query.Trim().ToLowerInvariant());
            VerdictLine = match is null ? "no match." : $"{match.Verdict} · {match.SetName} · {match.Label}";
        }
        catch (Exception ex)
        {
            VerdictLine = $"Failed: {ex.Message}";
        }
    }
}

// =====================================================================================
// YARA — minimal scan (uses pure-managed CRC32 to demonstrate flow; real yara via sidecar).
// =====================================================================================

public sealed partial class YaraTool
{
    [ObservableProperty] private string _rules = """
rule example_strings {
    meta:
        author = "you"
        description = "Sample — flag any file that contains the literal 'cinder'"
    strings:
        $a = "cinder" ascii nocase
    condition:
        $a
}
""";

    [ObservableProperty] private string? _scanTarget;
    [ObservableProperty] private string? _statusLine;
    public ObservableCollection<string> Hits { get; } = new();

    [RelayCommand]
    private async Task PickAndScanAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Pick a file to scan");
        if (string.IsNullOrEmpty(path)) return;
        ScanTarget = path;
        Hits.Clear();
        try
        {
            // For Phase 6 the wired path is a python-yara sidecar (parsers/yara/). Until that
            // sidecar is bundled, fall back to a straight literal-pattern scan derived from the
            // rule's strings — gives a usable smoke test for "this binary contains X".
            var literals = ExtractLiterals(Rules);
            if (literals.Count == 0)
            {
                StatusLine = "No literal `$x = \"...\"` strings found in the rule. Wire python-yara to enable full rules.";
                return;
            }
            var bytes = await File.ReadAllBytesAsync(path, ct);
            foreach (var lit in literals)
            {
                var idx = IndexOfBytes(bytes, Encoding.UTF8.GetBytes(lit));
                if (idx >= 0)
                {
                    Hits.Add($"{lit} @ 0x{idx:X}");
                }
            }
            StatusLine = Hits.Count == 0 ? "No matches." : $"{Hits.Count} match(es).";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
    }

    private static List<string> ExtractLiterals(string rule)
    {
        var literals = new List<string>();
        var idx = 0;
        while (true)
        {
            idx = rule.IndexOf('"', idx);
            if (idx < 0) break;
            var end = rule.IndexOf('"', idx + 1);
            if (end < 0) break;
            literals.Add(rule[(idx + 1)..end]);
            idx = end + 1;
        }
        return literals;
    }

    private static int IndexOfBytes(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            var ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (hay[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }
}

// =====================================================================================
// VIRUSTOTAL — opt-in hash-only lookup.
// =====================================================================================

public sealed partial class VirusTotalTool
{
    private readonly HttpClient _http = new();

    [ObservableProperty] private string? _apiKey;
    [ObservableProperty] private string _hash = "";
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private async Task LookupAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ApiKey))
        {
            StatusLine = "Set your VirusTotal API key first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Hash))
        {
            return;
        }
        try
        {
            var client = new VirusTotalClient(_http, () => ApiKey);
            var report = await client.LookupAsync(Hash.Trim().ToLowerInvariant(), ct);
            if (report is null)
            {
                ResultText = "No record / not seen.";
                StatusLine = client.QuotaExceeded ? "Quota exceeded." : "OK.";
            }
            else
            {
                ResultText = $"""
                    Hash: {report.Hash}
                    Malicious: {report.Malicious}
                    Suspicious: {report.Suspicious}
                    Harmless: {report.Harmless}
                    Undetected: {report.Undetected}
                    First seen: {report.FirstSubmissionUtc}
                    Last analyzed: {report.LastAnalysisUtc}
                    """;
                StatusLine = "OK.";
            }
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
    }
}

// =====================================================================================
// MAP — list of geo points, manual add.
// =====================================================================================

public sealed partial class MapTool
{
    public GeoIndex Index { get; } = new();
    public ObservableCollection<GeoPoint> Points { get; } = new();

    [ObservableProperty] private double _latitude;
    [ObservableProperty] private double _longitude;
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _source = "manual";

    [RelayCommand]
    private void AddPoint()
    {
        var p = new GeoPoint(Latitude, Longitude, DateTimeOffset.UtcNow, Label, Source, null);
        Index.Add(p);
        Points.Add(p);
        Latitude = 0; Longitude = 0; Label = "";
    }

    [RelayCommand]
    private void Clear()
    {
        Points.Clear();
    }
}

// =====================================================================================
// GRAPH — communication graph builder.
// =====================================================================================

public sealed partial class GraphTool
{
    public CommunicationGraph Graph { get; } = new();
    public ObservableCollection<GraphNode> Nodes { get; } = new();
    public ObservableCollection<GraphEdge> Edges { get; } = new();

    [ObservableProperty] private string _from = "";
    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _source = "manual";
    [ObservableProperty] private string? _subject;

    [RelayCommand]
    private void AddEdge()
    {
        if (string.IsNullOrWhiteSpace(From) || string.IsNullOrWhiteSpace(To)) return;
        Graph.AddInteraction(From, To, Source, DateTimeOffset.UtcNow, Subject);
        Refresh();
        From = ""; To = ""; Subject = "";
    }

    private void Refresh()
    {
        Nodes.Clear();
        foreach (var n in Graph.Nodes) Nodes.Add(n);
        Edges.Clear();
        foreach (var e in Graph.Edges) Edges.Add(e);
    }
}

// =====================================================================================
// ACQUIRE — Imager / Verify / Mount / Convert / VSS / RAM / Carver / Cloud
// =====================================================================================

public sealed partial class ImagerTool
{
    [ObservableProperty] private string? _source;
    [ObservableProperty] private string? _output;
    [ObservableProperty] private string _format = "Raw";
    [ObservableProperty] private string? _statusLine;
    [ObservableProperty] private long _bytesRead;

    public IReadOnlyList<string> Formats { get; } = ["Raw", "Ewf", "Aff4", "Vhd", "Vhdx"];

    [RelayCommand]
    private async Task PickSourceAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Pick a source disk image / file");
        if (!string.IsNullOrEmpty(path)) Source = path;
    }

    [RelayCommand]
    private async Task PickOutputAsync(CancellationToken ct)
    {
        var ext = Format switch { "Ewf" => ".E01", "Aff4" => ".af4", "Vhd" => ".vhd", "Vhdx" => ".vhdx", _ => ".dd" };
        var path = await ToolDialog.SaveFileAsync("Output image", $"image{ext}", ext.TrimStart('.'));
        if (!string.IsNullOrEmpty(path)) Output = path;
    }

    [RelayCommand]
    private async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Source) || string.IsNullOrEmpty(Output))
        {
            StatusLine = "Pick a source and an output path.";
            return;
        }
        StatusLine = "Imaging via parsers/imager sidecar (requires libewf-python for E01)…";
        try
        {
            var parsers = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "parsers");
            var imager = new SidecarDiskImager(() => SidecarDiskImager.DefaultSidecar(parsers));
            var fmt = Enum.Parse<ImageFormat>(Format);
            var job = new ImageJob(Source, Output, fmt);
            var progress = new Progress<ImageJobProgress>(p => BytesRead = p.BytesRead);
            var result = await imager.ImageAsync(job, progress, ct);
            StatusLine = $"Done. SHA-256 {result.Sha256?[..16]}… · {result.BytesWritten:N0} bytes · {result.BadSectors} bad sectors";
        }
        catch (Exception ex)
        {
            StatusLine = $"Imaging failed: {ex.Message}. (Sidecar requires Python + libewf-python.)";
        }
    }
}

public sealed partial class VerifyTool
{
    [ObservableProperty] private string? _imagePath;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private async Task PickAndRunAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Pick image to verify");
        if (string.IsNullOrEmpty(path)) return;
        ImagePath = path;
        StatusLine = "Verifying…";
        try
        {
            var parsers = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "parsers");
            var verifier = new ImageVerifier(() => SidecarDiskImager.DefaultSidecar(parsers));
            var r = await verifier.VerifyAsync(path, ct: ct);
            ResultText = $"""
                Match: {r.Match}
                Expected SHA-256: {r.ExpectedSha256 ?? "—"}
                Actual   SHA-256: {r.ActualSha256 ?? "—"}
                Bytes verified: {r.BytesVerified:N0}
                """;
            StatusLine = r.Match ? "✓ verified" : "⚠ mismatch";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
    }
}

public sealed partial class MountTool
{
    [ObservableProperty] private string? _imagePath;
    [ObservableProperty] private string? _mountedAt;
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private async Task PickAndMountAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Pick image to mount");
        if (string.IsNullOrEmpty(path)) return;
        ImagePath = path;
        try
        {
            var parsers = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "parsers");
            var m = ImageMounterFactory.ForCurrentPlatform(parsers);
            var handle = await m.MountReadOnlyAsync(path, ct);
            MountedAt = handle.MountPoint;
            StatusLine = $"Mounted at {handle.MountPoint}";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
    }
}

public sealed partial class ConvertTool
{
    [ObservableProperty] private string? _source;
    [ObservableProperty] private string? _output;
    [ObservableProperty] private string _format = "raw";
    [ObservableProperty] private string? _statusLine;

    public IReadOnlyList<string> Formats { get; } = ["raw", "e01"];

    [RelayCommand]
    private async Task PickSourceAsync(CancellationToken ct)
    {
        var p = await ToolDialog.PickFileAsync("Source image");
        if (!string.IsNullOrEmpty(p)) Source = p;
    }

    [RelayCommand]
    private async Task PickOutputAsync(CancellationToken ct)
    {
        var p = await ToolDialog.SaveFileAsync("Output", "out.dd", "dd");
        if (!string.IsNullOrEmpty(p)) Output = p;
    }

    [RelayCommand]
    private void Run()
    {
        StatusLine = "Image format conversion runs through parsers/imager (requires libewf-python). Use the Imager tab to drive it.";
    }
}

public sealed partial class ShadowCopyTool
{
    [ObservableProperty] private string? _statusLine;
    public ObservableCollection<Cinder.Native.ShadowCopy> Snapshots { get; } = new();

    [RelayCommand]
    private void Refresh()
    {
        Snapshots.Clear();
        try
        {
            var enumr = ShadowCopyService.ForCurrentPlatform();
            foreach (var s in enumr.Enumerate())
            {
                Snapshots.Add(s);
            }
            StatusLine = $"{Snapshots.Count} snapshot{(Snapshots.Count == 1 ? "" : "s")} found.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
    }
}

public sealed partial class RamCaptureTool
{
    [ObservableProperty] private string? _output;
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private async Task PickAndCaptureAsync(CancellationToken ct)
    {
        var path = await ToolDialog.SaveFileAsync("RAM dump output", "memory.raw", "raw");
        if (string.IsNullOrEmpty(path)) return;
        Output = path;
        StatusLine = "RAM capture: Cinder's signed kernel driver is source-only today. Drop winpmem.exe alongside Cinder for the Windows fallback. Linux: use LiME (see drivers/cinder-ram-linux/README.md).";
        _ = ct;
    }
}

public sealed partial class CarverTool
{
    [ObservableProperty] private string? _source;
    [ObservableProperty] private string? _outputDir;
    [ObservableProperty] private string? _statusLine;

    public ObservableCollection<Cinder.Carving.CarveHit> Hits { get; } = new();

    [RelayCommand]
    private async Task PickSourceAsync(CancellationToken ct)
    {
        var p = await ToolDialog.PickFileAsync("Source to carve");
        if (!string.IsNullOrEmpty(p)) Source = p;
    }

    [RelayCommand]
    private async Task PickOutputDirAsync(CancellationToken ct)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Output folder" });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) OutputDir = path;
    }

    [RelayCommand]
    private async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Source) || string.IsNullOrEmpty(OutputDir))
        {
            StatusLine = "Pick a source and an output folder.";
            return;
        }
        Hits.Clear();
        StatusLine = "Carving…";
        try
        {
            var carver = new Cinder.Carving.FileCarver();
            await using var fs = File.OpenRead(Source);
            await foreach (var hit in carver.CarveAsync(fs, OutputDir, ct: ct))
            {
                Hits.Add(hit);
                if (Hits.Count >= 10_000) break;
            }
            StatusLine = $"{Hits.Count:N0} carved.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
    }
}

public sealed partial class CloudPullTool
{
    [ObservableProperty] private string _provider = "google-drive";
    [ObservableProperty] private string? _statusLine;
    public IReadOnlyList<string> Providers { get; } = ["google-drive", "onedrive", "dropbox"];

    [RelayCommand]
    private void Connect() =>
        StatusLine = "Cloud connectors require user-supplied OAuth client_ids — see docs/cloud-setup.md, then paste them into Settings → Cloud.";
}

// =====================================================================================
// WORKFLOWS — load JSON + view nodes.
// =====================================================================================

public sealed partial class WorkflowsTool
{
    public ObservableCollection<Cinder.Workflow.WorkflowNode> Nodes { get; } = new();

    [ObservableProperty] private string? _path;
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private async Task LoadAsync(CancellationToken ct)
    {
        var p = await ToolDialog.PickFileAsync("Open workflow", "Workflow JSON", "*.json");
        if (string.IsNullOrEmpty(p)) return;
        Path = p;
        Nodes.Clear();
        try
        {
            var json = await File.ReadAllTextAsync(p, ct);
            var wf = Cinder.Workflow.Workflow.FromJson(json);
            foreach (var n in wf.TopologicalOrder()) Nodes.Add(n);
            StatusLine = $"{Nodes.Count} step{(Nodes.Count == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
    }
}

// =====================================================================================
// PLUGINS — load .dll from a folder.
// =====================================================================================

public sealed partial class PluginsTool
{
    public ObservableCollection<Cinder.Plugins.PluginLoadResult> Loaded { get; } = new();

    [ObservableProperty] private string? _folder;
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private async Task PickFolderAsync(CancellationToken ct)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Plugins folder" });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        Folder = path;
        Loaded.Clear();
        try
        {
            var loader = new Cinder.Plugins.PluginLoader();
            foreach (var p in loader.LoadFromDirectory(path))
            {
                Loaded.Add(p);
            }
            var loadedCount = Loaded.Count(r => r.Status == "loaded");
            var untrustedCount = Loaded.Count(r => r.Status == "untrusted");
            var failedCount = Loaded.Count(r => r.Status == "failed");
            StatusLine = $"{loadedCount} loaded · {untrustedCount} untrusted · {failedCount} failed";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
        _ = ct;
    }
}

// =====================================================================================
// Settings — surfaced as a button that opens the existing dialog.
// =====================================================================================

public sealed partial class SettingsTool
{
    [RelayCommand]
    private void OpenSettings()
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        var dialog = new Cinder.App.Views.SettingsDialog
        {
            DataContext = new SettingsDialogViewModel(new SettingsStore()),
        };
        dialog.ShowDialog(owner);
    }
}

// =====================================================================================
// Shared dialog helper.
// =====================================================================================

internal static class ToolDialog
{
    public static async Task<string?> PickFileAsync(string title, string? typeName = null, string? pattern = null)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return null;
        var opts = new FilePickerOpenOptions { Title = title, AllowMultiple = false };
        if (typeName is not null && pattern is not null)
        {
            opts.FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = [pattern] }];
        }
        var picked = await owner.StorageProvider.OpenFilePickerAsync(opts);
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> SaveFileAsync(string title, string suggestedName, string defaultExtension)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return null;
        var picked = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = defaultExtension,
        });
        return picked?.TryGetLocalPath();
    }
}
