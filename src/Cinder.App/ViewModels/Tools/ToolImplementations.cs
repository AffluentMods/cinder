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
    /// <summary>Full result set from the last scan. Filtered into <see cref="Hits"/> by the UI.</summary>
    private readonly List<StringHit> _all = new();

    /// <summary>What's actually shown in the DataGrid — already filtered by search + gibberish toggles.</summary>
    public ObservableCollection<StringHit> Hits { get; } = new();

    [ObservableProperty] private string? _path;
    [ObservableProperty] private int _minLength = 6;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _statusLine;

    /// <summary>Live substring filter — re-applies as the user types.</summary>
    [ObservableProperty] private string? _filter;

    /// <summary>Hide strings that look like compressed-byte coincidence — no letters, mostly punctuation.</summary>
    [ObservableProperty] private bool _hideGibberish = true;

    /// <summary>One-line callout above the result grid if the file looks like a container (ZIP, gzip, etc).</summary>
    [ObservableProperty] private string? _containerHint;

    partial void OnFilterChanged(string? value) => Reproject();
    partial void OnHideGibberishChanged(bool value) => Reproject();

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
        _all.Clear();
        Hits.Clear();
        ContainerHint = null;
        IsLoading = true;
        StatusLine = "Scanning…";
        try
        {
            ContainerHint = await Task.Run(() => DetectContainer(Path), ct);
            var minLen = Math.Max(3, MinLength);
            var hits = await Task.Run(() => Extract(Path, minLen, ct), ct);
            _all.AddRange(hits);
            Reproject();
            StatusLine = BuildStatus();
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

    /// <summary>Rebuild <see cref="Hits"/> from <see cref="_all"/> using the current filter/gibberish toggle.</summary>
    private void Reproject()
    {
        Hits.Clear();
        var needle = (Filter ?? "").Trim();
        var hasNeedle = needle.Length > 0;
        var hideJunk = HideGibberish;
        var shown = 0;
        foreach (var h in _all)
        {
            if (hideJunk && IsGibberish(h.Value))
            {
                continue;
            }
            if (hasNeedle && h.Value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }
            Hits.Add(h);
            if (++shown >= 50_000) break;
        }
        StatusLine = BuildStatus();
    }

    private string BuildStatus()
    {
        if (_all.Count == 0)
        {
            return Hits.Count == 0 ? "" : $"{Hits.Count:N0}";
        }
        return Hits.Count == _all.Count
            ? $"{_all.Count:N0} string{(_all.Count == 1 ? "" : "s")}"
            : $"{Hits.Count:N0} of {_all.Count:N0} (filtered)";
    }

    /// <summary>
    /// A string looks like "gibberish" — i.e. compressed-byte coincidence — when it contains no
    /// ASCII letter, OR when more than two-thirds of its characters are punctuation/symbol. Plain
    /// English words like "Hello" pass; random four-byte ASCII runs like ":*eJ" or "y'Hn-" fail.
    /// </summary>
    internal static bool IsGibberish(string s)
    {
        int letters = 0, punctuationOrSymbol = 0;
        foreach (var c in s)
        {
            if (char.IsLetter(c)) letters++;
            else if (char.IsPunctuation(c) || char.IsSymbol(c)) punctuationOrSymbol++;
        }
        if (letters == 0) return true;
        // 7+ chars: looser threshold (compressed runs of that length are rare anyway).
        var punctRatio = (double)punctuationOrSymbol / s.Length;
        if (s.Length <= 6)
        {
            return letters < 3 || punctRatio > 0.4;
        }
        return punctRatio > 0.55;
    }

    /// <summary>
    /// Sniffs the first few bytes to identify common container formats. Returns null if the file
    /// doesn't look like a known container. The caller surfaces this as a banner so users opening
    /// a .docx / .zip / .tar.gz aren't confused when they see only metadata strings.
    /// </summary>
    internal static string? DetectContainer(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[8];
            var n = fs.Read(head);
            if (n < 4) return null;

            // ZIP — also covers .docx / .xlsx / .pptx / .jar / .apk / .epub / .odt.
            if (head[0] == 0x50 && head[1] == 0x4B && (head[2] == 0x03 || head[2] == 0x05 || head[2] == 0x07))
            {
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                return ext switch
                {
                    ".docx" or ".docm" => "This is a Word document (a ZIP container). The strings shown are the container's internal filenames — the actual body text lives inside compressed streams. Try the Documents tool to read the content.",
                    ".xlsx" or ".xlsm" => "This is an Excel workbook (a ZIP container). Cell text lives inside compressed streams. Try a dedicated XLSX viewer for the cell data.",
                    ".pptx" => "This is a PowerPoint file (a ZIP container). Slide text lives inside compressed streams.",
                    ".jar" or ".apk" => "This is a JAR/APK (ZIP). Strings shown are container metadata — extract first to read class/code strings.",
                    ".epub" or ".odt" or ".ods" or ".odp" => "This is an OpenDocument / EPUB (ZIP container). Use a dedicated viewer for the document body.",
                    _ => "This file is a ZIP container. The strings shown are filenames + central-directory metadata, not the compressed contents.",
                };
            }
            // gzip
            if (head[0] == 0x1F && head[1] == 0x8B)
            {
                return "This file is gzip-compressed. Strings shown are gzip headers; the body is compressed.";
            }
            // bzip2
            if (head[0] == 0x42 && head[1] == 0x5A && head[2] == 0x68)
            {
                return "This file is bzip2-compressed. Strings shown are bzip2 headers; the body is compressed.";
            }
            // xz
            if (head[0] == 0xFD && head[1] == 0x37 && head[2] == 0x7A && head[3] == 0x58 && head[4] == 0x5A)
            {
                return "This file is xz-compressed. Strings shown are xz headers; the body is compressed.";
            }
            // 7-Zip
            if (head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C)
            {
                return "This file is a 7z archive. Strings shown are archive metadata; entries are compressed.";
            }
            // RAR
            if (head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72 && head[3] == 0x21)
            {
                return "This file is a RAR archive. Strings shown are archive metadata; entries are compressed.";
            }
            // PDF (not compressed end-to-end but content is largely DEFLATE-streamed)
            if (head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46)
            {
                return "This is a PDF. Most body text is wrapped in compressed streams — strings shown include object dictionaries and uncompressed metadata only.";
            }
            // ELF / Mach-O / PE — informational, not blocking.
            if (head[0] == 0x7F && head[1] == 0x45 && head[2] == 0x4C && head[3] == 0x46)
            {
                return "This is a Linux ELF binary. Look for hardcoded URLs, debug symbols, and API names in the strings below.";
            }
            if (head[0] == 0x4D && head[1] == 0x5A)
            {
                return "This is a Windows PE executable. Look for hardcoded URLs, imports, and embedded resources in the strings below.";
            }
        }
        catch
        {
            // Unreadable file — let the main scan surface the error.
        }
        return null;
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
    [ObservableProperty] private bool _isLoading;

    [RelayCommand]
    private async Task PickAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Pick a document");
        if (string.IsNullOrEmpty(path)) return;
        Path = path;
        IsLoading = true;
        StatusLine = "Extracting…";
        Content = null;
        try
        {
            var result = await DocumentReader.ReadAsync(path, ct);
            if (result.Success)
            {
                Content = string.IsNullOrEmpty(result.Text)
                    ? "(no extractable text in this document)"
                    : result.Text;
                StatusLine = result.Status;
            }
            else
            {
                // Surface the friendly explanation as the body so the user sees why nothing
                // came back — and the status line stays clean.
                Content = result.Status;
                StatusLine = $"{new FileInfo(path).Length:N0} bytes · no preview";
            }
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
            Content = null;
        }
        finally
        {
            IsLoading = false;
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
        description = "Flag any file that contains the literal 'cinder' or the PE magic"
    strings:
        $a = "cinder" nocase
        $b = "malware"
        $mz = { 4D 5A }
    condition:
        any of them
}
""";

    [ObservableProperty] private string? _scanTarget;
    [ObservableProperty] private string? _statusLine;
    [ObservableProperty] private bool _isScanning;

    public ObservableCollection<YaraHitRow> Hits { get; } = new();
    public ObservableCollection<RuleSummaryRow> RuleSummary { get; } = new();

    [RelayCommand]
    private async Task LoadRulesFileAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Pick a YARA rules file", "YARA rules", "*.yar");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Rules = await File.ReadAllTextAsync(path, ct);
            StatusLine = $"Loaded {path}";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed to read {path}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PickAndScanAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Pick a file to scan");
        if (string.IsNullOrEmpty(path)) return;
        ScanTarget = path;
        Hits.Clear();
        RuleSummary.Clear();
        IsScanning = true;
        StatusLine = "Scanning…";
        try
        {
            var parsed = await Task.Run(() => YaraLiteParser.Parse(Rules), ct);
            if (parsed.Count == 0)
            {
                StatusLine = "No rules parsed from the editor. Check the syntax.";
                return;
            }
            var ruleset = await Task.Run(() => YaraLiteRuleset.Compile(parsed), ct);
            await foreach (var hit in ruleset.ScanAsync(path, ct))
            {
                Hits.Add(new YaraHitRow(
                    Rule: hit.RuleName,
                    Identifier: hit.Identifier,
                    OffsetHex: $"0x{hit.Offset:X12}",
                    Matched: hit.MatchedString));
                if (Hits.Count >= 25_000) break;
            }
            // Per-rule summary including skip reasons.
            foreach (var r in parsed)
            {
                var ruleHits = Hits.Count(h => h.Rule == r.Name);
                RuleSummary.Add(new RuleSummaryRow(
                    Name: r.Name,
                    Status: r.SkipReason is not null ? "skipped"
                          : ruleHits > 0 ? "matched"
                          : "no match",
                    Hits: ruleHits,
                    Note: r.SkipReason ?? (r.Strings.Count == 0 ? "no strings" : "")));
            }
            StatusLine = Hits.Count == 0
                ? $"No matches in {System.IO.Path.GetFileName(path)} across {parsed.Count} rule(s)."
                : $"{Hits.Count} match(es) across {RuleSummary.Count(r => r.Status == "matched")} rule(s).";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }
}

public sealed record YaraHitRow(string Rule, string Identifier, string OffsetHex, string Matched);
public sealed record RuleSummaryRow(string Name, string Status, int Hits, string Note);

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
    [ObservableProperty] private string? _statusLine;

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
        Index.Clear();
    }

    /// <summary>
    /// Walk a folder of images, extract GPS coordinates from EXIF, and add every geo-tagged
    /// photo as a point. Pure C# via MetadataExtractor — no Python, no shell-outs.
    /// </summary>
    [RelayCommand]
    private async Task IngestPhotosAsync(CancellationToken ct)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick a folder of photos to map",
        });
        var root = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(root)) return;

        StatusLine = "Scanning photos…";
        var added = await Task.Run(() => ScanFolderForExifGps(root, Index, Points, ct), ct);
        StatusLine = $"Added {added:N0} GPS point{(added == 1 ? "" : "s")} from {root}.";
    }

    private static int ScanFolderForExifGps(
        string root,
        GeoIndex index,
        ObservableCollection<GeoPoint> points,
        CancellationToken ct)
    {
        int added = 0;
        var exts = new[] { ".jpg", ".jpeg", ".heic", ".heif", ".tiff", ".tif", ".png", ".webp" };
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        }))
        {
            ct.ThrowIfCancellationRequested();
            if (added >= 25_000) break;
            if (!exts.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant())) continue;
            try
            {
                var dirs = MetadataExtractor.ImageMetadataReader.ReadMetadata(path);
                var gps = dirs.OfType<MetadataExtractor.Formats.Exif.GpsDirectory>().FirstOrDefault();
                if (gps is null) continue;
                var maybeLoc = gps.GetGeoLocation();
                if (maybeLoc is not MetadataExtractor.GeoLocation loc || loc.IsZero) continue;
                // Use file mtime as a proxy for the photo's timestamp — pulling DateTimeOriginal
                // out of EXIF requires the extension-method path which varies across
                // MetadataExtractor versions; mtime is good enough for plotting.
                var ts = new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc, TimeSpan.Zero);
                var p = new GeoPoint(loc.Latitude, loc.Longitude, ts,
                    System.IO.Path.GetFileName(path), "exif", null);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    index.Add(p);
                    points.Add(p);
                });
                added++;
            }
            catch
            {
                // unreadable image — skip
            }
        }
        return added;
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
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private void AddEdge()
    {
        if (string.IsNullOrWhiteSpace(From) || string.IsNullOrWhiteSpace(To)) return;
        Graph.AddInteraction(From, To, Source, DateTimeOffset.UtcNow, Subject);
        Refresh();
        From = ""; To = ""; Subject = "";
    }

    [RelayCommand]
    private void ClearGraph()
    {
        Graph.Clear();
        Refresh();
    }

    /// <summary>
    /// Walk a folder of email files (.eml / .msg / .mbox) and auto-build the communication
    /// graph from From / To / Cc headers. Pure C# via MsgReader and an in-house MBOX scanner.
    /// </summary>
    [RelayCommand]
    private async Task IngestEmailFolderAsync(CancellationToken ct)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick a folder containing .eml / .msg / .mbox files",
        });
        var root = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(root)) return;

        StatusLine = "Ingesting…";
        var n = await Task.Run(() => ScanFolderForEmail(root, Graph, ct), ct);
        Refresh();
        StatusLine = $"Added {n:N0} interaction{(n == 1 ? "" : "s")} from {root}.";
    }

    private static int ScanFolderForEmail(string root, CommunicationGraph graph, CancellationToken ct)
    {
        int added = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        }))
        {
            ct.ThrowIfCancellationRequested();
            if (added >= 25_000) break;
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            try
            {
                switch (ext)
                {
                    case ".msg":
                        IngestMsg(path, graph, ref added);
                        break;
                    case ".eml":
                        IngestEml(path, graph, ref added);
                        break;
                    case ".mbox":
                        IngestMbox(path, graph, ref added, ct);
                        break;
                }
            }
            catch
            {
                // unreadable / parse error — skip
            }
        }
        return added;
    }

    private static void IngestMsg(string path, CommunicationGraph graph, ref int added)
    {
        using var msg = new MsgReader.Outlook.Storage.Message(path);
        var from = msg.GetEmailSender(false, false) ?? "";
        var to = msg.GetEmailRecipients(MsgReader.Outlook.RecipientType.To, false, false) ?? "";
        var subject = msg.Subject ?? "";
        // MsgReader's SentOn surface varies — coerce whatever DateTime/DateTimeOffset it
        // hands us into a DateTimeOffset.
        DateTimeOffset ts = CoerceToUtcOffset(msg.SentOn);
        AddFromHeader(graph, from, to, "msg", ts, subject, ref added);
    }

    private static void IngestEml(string path, CommunicationGraph graph, ref int added)
    {
        using var fs = File.OpenRead(path);
        var msg = MsgReader.Mime.Message.Load(fs);
        var from = msg.Headers.From?.Address ?? "";
        var to = string.Join(", ", msg.Headers.To?.Select(t => t.Address) ?? Array.Empty<string>());
        var subject = msg.Headers.Subject ?? "";
        DateTimeOffset ts = CoerceToUtcOffset(msg.Headers.DateSent);
        AddFromHeader(graph, from, to, "eml", ts, subject, ref added);
    }

    /// <summary>
    /// Normalise whatever shape (DateTime, DateTime?, DateTimeOffset, DateTimeOffset?) a third
    /// party hands us into a UTC DateTimeOffset, defaulting to now if null/empty.
    /// </summary>
    private static DateTimeOffset CoerceToUtcOffset(object? value)
    {
        return value switch
        {
            DateTimeOffset dto => dto.ToUniversalTime(),
            DateTime dt when dt != default =>
                new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
            _ => DateTimeOffset.UtcNow,
        };
    }

    private static void IngestMbox(string path, CommunicationGraph graph, ref int added, CancellationToken ct)
    {
        using var sr = new StreamReader(path);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        bool inHeaders = false;
        while ((line = sr.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (added >= 25_000) break;

            if (line.StartsWith("From ", StringComparison.Ordinal))
            {
                FlushMboxHeaders(graph, headers, ref added);
                inHeaders = true;
                continue;
            }
            if (inHeaders && string.IsNullOrEmpty(line))
            {
                inHeaders = false;
                continue;
            }
            if (inHeaders)
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line[..colon]] = line[(colon + 1)..].Trim();
                }
            }
        }
        FlushMboxHeaders(graph, headers, ref added);
    }

    private static void FlushMboxHeaders(CommunicationGraph graph, Dictionary<string, string> headers, ref int added)
    {
        if (headers.Count == 0) return;
        var from = headers.GetValueOrDefault("From", "");
        var to = headers.GetValueOrDefault("To", "");
        var subject = headers.GetValueOrDefault("Subject", "");
        DateTimeOffset ts = DateTimeOffset.UtcNow;
        if (headers.TryGetValue("Date", out var d) && DateTimeOffset.TryParse(d, out var parsed))
        {
            ts = parsed.ToUniversalTime();
        }
        AddFromHeader(graph, from, to, "mbox", ts, subject, ref added);
        headers.Clear();
    }

    private static void AddFromHeader(CommunicationGraph graph, string from, string to,
                                       string source, DateTimeOffset ts, string subject, ref int added)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return;
        // To header can be a comma-separated list — emit one edge per recipient.
        foreach (var recipient in to.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            graph.AddInteraction(
                ExtractAddress(from),
                ExtractAddress(recipient),
                source,
                ts,
                subject);
            added++;
        }
    }

    private static string ExtractAddress(string headerValue)
    {
        // Headers come in two shapes: "Name <foo@bar>" and "foo@bar". Pull just the address.
        var s = headerValue.Trim();
        var lt = s.IndexOf('<');
        var gt = s.IndexOf('>');
        if (lt >= 0 && gt > lt)
        {
            return s[(lt + 1)..gt].Trim();
        }
        return s;
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

    /// <summary>One row of execution output per node, populated by the Run command.</summary>
    public ObservableCollection<WorkflowExecutionRow> Outputs { get; } = new();

    [ObservableProperty] private string? _path;
    [ObservableProperty] private string? _statusLine;
    [ObservableProperty] private bool _isRunning;

    private Cinder.Workflow.Workflow? _loaded;

    [RelayCommand]
    private async Task LoadAsync(CancellationToken ct)
    {
        var p = await ToolDialog.PickFileAsync("Open workflow", "Workflow JSON", "*.json");
        if (string.IsNullOrEmpty(p)) return;
        Path = p;
        Nodes.Clear();
        Outputs.Clear();
        try
        {
            var json = await File.ReadAllTextAsync(p, ct);
            _loaded = Cinder.Workflow.Workflow.FromJson(json);
            foreach (var n in _loaded.TopologicalOrder()) Nodes.Add(n);
            StatusLine = $"{Nodes.Count} step{(Nodes.Count == 1 ? "" : "s")} loaded.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Execute the loaded workflow. Each step's handler runs in topological order; results
    /// land in <see cref="Outputs"/> for the user to inspect. Failed steps abort the run with
    /// a status message but don't crash the tool.
    /// </summary>
    [RelayCommand]
    private async Task RunAsync(CancellationToken ct)
    {
        if (_loaded is null)
        {
            StatusLine = "Load a workflow first.";
            return;
        }
        Outputs.Clear();
        IsRunning = true;
        StatusLine = "Running…";
        try
        {
            var runner = WorkflowHandlers.BuildRunner(Outputs);
            await Task.Run(() => runner.RunAsync(_loaded, ct), ct);
            StatusLine = $"Done · {Outputs.Count} step{(Outputs.Count == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            Outputs.Add(new WorkflowExecutionRow("(error)", "—", "failed", ex.Message));
            StatusLine = $"Failed at step: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }
}

/// <summary>One execution row shown in the Workflows results pane.</summary>
public sealed record WorkflowExecutionRow(string NodeId, string Kind, string Status, string Result);

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
