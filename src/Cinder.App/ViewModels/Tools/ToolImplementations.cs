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

    /// <summary>Live filter — re-applies as the user types. Meaning depends on <see cref="FilterMode"/>.</summary>
    [ObservableProperty] private string? _filter;

    /// <summary>Hide strings that look like compressed-byte coincidence — no letters, mostly punctuation.</summary>
    [ObservableProperty] private bool _hideGibberish = true;

    /// <summary>One-line callout above the result grid if the file looks like a container (ZIP, gzip, etc).</summary>
    [ObservableProperty] private string? _containerHint;

    /// <summary>
    /// How <see cref="Filter"/> is applied: a plain substring, a .NET regex, or one of the
    /// <see cref="Cinder.Core.Analysis.FeatureExtractor"/> presets (emails, URLs, IPs, card
    /// numbers, wallet addresses, credential-shaped tokens). The presets are the
    /// bulk_extractor-style "what identifiers are in this blob" pass every triage starts with.
    /// </summary>
    [ObservableProperty] private FilterModeOption _filterMode = FilterModeOption.Substring;

    public IReadOnlyList<FilterModeOption> FilterModes { get; } =
    [
        FilterModeOption.Substring,
        FilterModeOption.Regex,
        .. Cinder.Core.Analysis.FeatureExtractor.Presets.Select(p => new FilterModeOption(p.Id, p.Name, p.Description)),
    ];

    /// <summary>Set when the current regex fails to compile; shown instead of a silent empty grid.</summary>
    [ObservableProperty] private string? _filterError;

    partial void OnFilterChanged(string? value) => Reproject();
    partial void OnHideGibberishChanged(bool value) => Reproject();
    partial void OnFilterModeChanged(FilterModeOption value) => Reproject();

    /// <summary>Writes the currently shown hits (after filter + gibberish toggle) to CSV or JSON.</summary>
    [RelayCommand]
    private async Task ExportAsync(string format, CancellationToken ct)
    {
        if (Hits.Count == 0) return;
        var ext = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "csv";
        var path = await ToolDialog.SaveFileAsync($"Export strings as {ext.ToUpperInvariant()}", $"strings-export.{ext}", ext);
        if (string.IsNullOrEmpty(path)) return;

        var snapshot = Hits.Cast<object>().ToList();
        try
        {
            var written = await Task.Run(() =>
            {
                if (ext == "json")
                {
                    using var fs = File.Create(path);
                    return Cinder.Core.Export.TabularExporter.WriteJson(snapshot, fs);
                }
                using var w = new StreamWriter(path, append: false, Encoding.UTF8);
                return Cinder.Core.Export.TabularExporter.WriteCsv(snapshot, w);
            }, ct);
            StatusLine = $"Exported {written:N0} strings → {path}";
            await ActiveCaseContext.LogAsync(CustodyAction.DataExported, new
            {
                Tool = "strings",
                Format = ext,
                Path = path,
                Rows = written,
                Evidence = Path,
                Mode = FilterMode.Id,
                Filter,
            }, ct);
        }
        catch (Exception ex)
        {
            StatusLine = $"Export failed: {ex.Message}";
        }
    }

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
        FilterError = null;
        var needle = (Filter ?? "").Trim();
        var hideJunk = HideGibberish;

        // Build one predicate up front so the per-row loop stays a single call.
        Func<string, bool> accept;
        if (FilterMode == FilterModeOption.Regex)
        {
            if (needle.Length == 0)
            {
                accept = static _ => true;
            }
            else
            {
                try
                {
                    var rx = new System.Text.RegularExpressions.Regex(
                        needle,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(250));
                    accept = v =>
                    {
                        try { return rx.IsMatch(v); }
                        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return false; }
                    };
                }
                catch (ArgumentException ex)
                {
                    FilterError = $"Invalid regex: {ex.Message}";
                    StatusLine = BuildStatus();
                    return;
                }
            }
        }
        else if (FilterMode == FilterModeOption.Substring)
        {
            accept = needle.Length == 0
                ? static _ => true
                : v => v.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        else
        {
            var preset = Cinder.Core.Analysis.FeatureExtractor.Find(FilterMode.Id);
            if (preset is null)
            {
                accept = static _ => true;
            }
            else if (needle.Length == 0)
            {
                accept = preset.IsMatch;
            }
            else
            {
                // Preset AND substring: "emails containing example.com".
                accept = v => preset.IsMatch(v) && v.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        var shown = 0;
        foreach (var h in _all)
        {
            if (hideJunk && IsGibberish(h.Value))
            {
                continue;
            }
            if (!accept(h.Value))
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

/// <summary>One entry in the Strings tool's filter-mode picker.</summary>
public sealed record FilterModeOption(string Id, string Label, string Description)
{
    public static readonly FilterModeOption Substring = new("substring", "Contains text", "Case-insensitive substring");
    public static readonly FilterModeOption Regex = new("regex", "Regex", ".NET regular expression, case-insensitive");
    public override string ToString() => Label;
}

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

    /// <summary>Signed tip attestations found in the file, each with its verification result.</summary>
    public ObservableCollection<AttestationVerification> Attestations { get; } = new();

    [ObservableProperty] private string? _attestationVerdict;
    [ObservableProperty] private string? _keyFingerprint;

    private Guid? _loadedCaseId;

    /// <summary>
    /// Signs the chain's current tip with the examiner key held in this user's profile. The
    /// chain alone is unkeyed and lives in the case file; the attestation is what makes a
    /// later rewrite detectable by anyone holding the file. Export it and send it somewhere
    /// you do not control to anchor it against yourself as well.
    /// </summary>
    [RelayCommand]
    private async Task SignTipAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(CasePath) || _loadedCaseId is not { } caseId)
        {
            StatusLine = "Open + verify a case first.";
            return;
        }
        try
        {
            var signer = new CustodySigner(new CaseStore(CasePath));
            var a = await signer.SignTipAsync(caseId, Environment.UserName, ct);
            KeyFingerprint = a.KeyFingerprint;
            StatusLine = $"Signed tip: sequence {a.Sequence}, hash {a.EntryHash[..16]}…, key {a.KeyFingerprint}. Export it to anchor the chain outside this file.";

            // Countersign with a Time-Stamp Authority when one is configured: the examiner
            // key says who, the TSA says no later than when, from a clock that is not ours.
            var tsaUrl = new SettingsStore().Load().TimestampAuthorityUrl;
            if (!string.IsNullOrWhiteSpace(tsaUrl))
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    var stamped = await signer.TimestampAsync(a.Id, new Uri(tsaUrl), http, ct);
                    StatusLine += $" Timestamped by {tsaUrl} at {stamped.TsaTime:u}.";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    StatusLine += $" Timestamp request to {tsaUrl} failed ({ex.Message}); the attestation is signed but not timestamped.";
                }
            }
            await ReloadAttestationsAsync(signer, caseId, ct);
        }
        catch (Exception ex)
        {
            StatusLine = $"Signing failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportAttestationAsync(CancellationToken ct)
    {
        var latest = Attestations.LastOrDefault()?.Attestation;
        if (latest is null)
        {
            StatusLine = "No attestation to export — sign the chain tip first.";
            return;
        }
        var path = await ToolDialog.SaveFileAsync("Export custody attestation", $"attestation-seq{latest.Sequence}.json", "json");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            await File.WriteAllTextAsync(path, CustodySigner.ExportAttestation(latest), ct);
            StatusLine = $"Attestation written to {path}. Mail it, ticket it, or drop it on a WORM share — anywhere you cannot edit later.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Export failed: {ex.Message}";
        }
    }

    private async Task ReloadAttestationsAsync(CustodySigner signer, Guid caseId, CancellationToken ct)
    {
        Attestations.Clear();
        foreach (var v in await signer.VerifyAsync(caseId, ct))
        {
            Attestations.Add(v);
        }
        if (Attestations.Count == 0)
        {
            AttestationVerdict = "No signed attestations — the chain is self-checking only. Sign the tip to anchor it.";
            return;
        }
        var bad = Attestations.Where(a => !a.Ok).ToList();
        var stamped = Attestations.Count(a => a.Timestamp is { SignatureValid: true });
        var untrusted = Attestations.Count(a => a.Timestamp is { SignatureValid: true, ChainTrusted: false });
        AttestationVerdict = bad.Count == 0
            ? $"✓ {Attestations.Count} attestation(s) verify; latest covers sequence {Attestations[^1].Attestation.Sequence}"
              + (stamped > 0 ? $"; {stamped} carry an RFC 3161 timestamp" : "")
              + (untrusted > 0 ? $" ({untrusted} from a TSA whose root this machine does not trust — check the issuer)" : "")
            : $"✗ attestation at sequence {bad[0].Attestation.Sequence} fails: {bad[0].Reason}";
    }

    [RelayCommand]
    private async Task PickAndVerifyAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Open Cinder case", "Cinder case", "*.cinder");
        if (string.IsNullOrEmpty(path)) return;
        CasePath = path;
        Entries.Clear();
        Attestations.Clear();
        AttestationVerdict = null;
        _loadedCaseId = null;
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

                // Attestations catch what the chain cannot: a full rewrite that re-hashes
                // consistently. Verified with nothing but the file — the public key travels
                // inside each attestation.
                _loadedCaseId = caseId;
                var signer = new CustodySigner(store);
                await ReloadAttestationsAsync(signer, caseId, ct);
                try { KeyFingerprint = File.Exists(signer.KeyPath) ? signer.KeyFingerprint() : null; } catch { KeyFingerprint = null; }
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDatabase))]
    private string? _databasePath;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _algorithm = "sha1";

    public bool HasDatabase => !string.IsNullOrEmpty(DatabasePath);

    /// <summary>Hex digits a digest of each algorithm has, to catch a paste of the wrong thing.</summary>
    private static readonly Dictionary<string, int> DigestHexLength = new(StringComparer.OrdinalIgnoreCase)
    {
        ["md5"] = 32, ["sha1"] = 40, ["sha256"] = 64, ["blake3"] = 64,
    };
    [ObservableProperty] private string? _verdictLine;
    [ObservableProperty] private string? _statusLine;

    public IReadOnlyList<string> Algorithms { get; } = ["md5", "sha1", "sha256", "blake3"];

    public HashSetsTool()
    {
        // Reopen the database the examiner picked last time, so lookups and the Filesystem
        // walk's verdicts work without re-picking after every launch.
        try
        {
            var saved = new SettingsStore().Load().HashSetDatabase;
            if (!string.IsNullOrEmpty(saved) && File.Exists(saved))
            {
                _service = new HashSetService(saved);
                DatabasePath = saved;
                StatusLine = "Hash set open.";
            }
        }
        catch { /* first launch or unreadable settings — pick manually */ }
    }

    [RelayCommand]
    private async Task PickDatabaseAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Open a hash set", "Hash-set database", "*.db");
        if (!string.IsNullOrEmpty(path)) OpenDatabase(path, created: false);
        _ = ct;
    }

    /// <summary>
    /// Creating needs a save dialog: an open dialog only lists files that already exist, so the
    /// old single "Open / create" button could never create anything.
    /// </summary>
    [RelayCommand]
    private async Task NewDatabaseAsync(CancellationToken ct)
    {
        var path = await ToolDialog.SaveFileAsync("Create a hash set", "hashsets.db", "db");
        if (!string.IsNullOrEmpty(path)) OpenDatabase(path, created: true);
        _ = ct;
    }

    private void OpenDatabase(string path, bool created)
    {
        try
        {
            _service?.Dispose();
            _service = new HashSetService(path);
            DatabasePath = path;
            VerdictLine = null;
            StatusLine = created
                ? "Empty hash set created. Import a list to fill it."
                : "Hash set open.";

            // Remember it: the Filesystem tool reads this to hand out Known / Unknown verdicts.
            var store = new SettingsStore();
            store.Save(store.Load() with { HashSetDatabase = path });
        }
        catch (Exception ex)
        {
            _service = null;
            DatabasePath = null;
            StatusLine = $"Could not open {path}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportNsrlAsync(CancellationToken ct)
    {
        if (_service is null)
        {
            StatusLine = "Open or create a hash set first.";
            return;
        }
        var path = await ToolDialog.PickFileAsync("Pick the NSRL minimal list (NSRLFile.txt or .csv)", "NSRL list", "*.txt;*.csv");
        if (string.IsNullOrEmpty(path)) return;
        StatusLine = "Importing… this can take several minutes for the full NSRL list.";
        try
        {
            var n = await Task.Run(() => _service.ImportNsrlMinimalCsv(path, $"NSRL_{DateTime.UtcNow:yyyyMMdd}"), ct);
            StatusLine = $"Imported {n:N0} hashes from {System.IO.Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Import failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Lookup()
    {
        if (_service is null)
        {
            StatusLine = "Open or create a hash set first.";
            return;
        }
        var digest = Query.Trim().ToLowerInvariant();
        if (digest.Length == 0)
        {
            return;
        }
        if (!digest.All(Uri.IsHexDigit))
        {
            VerdictLine = "That is not a hash: a hash contains only the characters 0-9 and a-f.";
            return;
        }
        if (DigestHexLength.TryGetValue(Algorithm, out var expected) && digest.Length != expected)
        {
            VerdictLine = $"A {Algorithm.ToUpperInvariant()} hash is {expected} characters; this one is {digest.Length}. Check the algorithm, or that the whole hash was pasted.";
            return;
        }
        try
        {
            var match = _service.Lookup(Algorithm, digest);
            VerdictLine = match is null
                ? "Not in this hash set. That does not make the file suspicious, only unlisted."
                : $"Match: {match.Verdict}\nList: {match.SetName}\n{match.Label}".TrimEnd();
        }
        catch (Exception ex)
        {
            VerdictLine = $"Lookup failed: {ex.Message}";
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
        ResultText = null;
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            StatusLine = "Enter your VirusTotal API key first. You can create a free one at virustotal.com under your profile.";
            return;
        }
        var hash = Hash.Trim().ToLowerInvariant();
        // Only a well-formed digest may leave the machine: the value goes into the request
        // path, and anything else (a file name, a path) would disclose it to a third party.
        if (hash.Length is not (32 or 40 or 64) || !hash.All(Uri.IsHexDigit))
        {
            StatusLine = "Enter an MD5 (32 characters), SHA-1 (40) or SHA-256 (64) hash. Nothing was sent.";
            return;
        }
        StatusLine = "Asking VirusTotal…";
        try
        {
            var client = new VirusTotalClient(_http, () => ApiKey.Trim());
            var report = await client.LookupAsync(hash, ct);
            if (report is null)
            {
                StatusLine = client.QuotaExceeded
                    ? "VirusTotal refused the request: your API key has used up its quota. Try again later."
                    : "VirusTotal has no record of this hash. That means nobody has submitted the file, not that it is safe.";
            }
            else
            {
                var total = report.Malicious + report.Suspicious + report.Harmless + report.Undetected;
                ResultText = $"""
                    Hash            {report.Hash}
                    Flagged by      {report.Malicious} of {total} engines as malicious, {report.Suspicious} as suspicious
                    Not flagged by  {report.Harmless + report.Undetected} engines
                    First seen      {report.FirstSubmissionUtc:yyyy-MM-dd HH:mm:ss} UTC
                    Last analysed   {report.LastAnalysisUtc:yyyy-MM-dd HH:mm:ss} UTC
                    """;
                StatusLine = "Lookup complete. The query is recorded in the custody log when a case is open.";
                await ActiveCaseContext.LogAsync(CustodyAction.Annotation,
                    new { Kind = "virustotal-lookup", Hash = hash, report.Malicious, report.Suspicious, Engines = total }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Lookup cancelled.";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            StatusLine = "VirusTotal rejected the API key. Check that it was pasted in full.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Could not reach VirusTotal: {ex.Message}";
        }
    }
}

// =====================================================================================
// MAP — list of geo points, manual add.
// =====================================================================================

public sealed partial class MapTool
{
    /// <summary>Hard stop for one ingest, so a huge photo share cannot grow the list without bound.</summary>
    private const int MaxPointsPerIngest = 25_000;

    public GeoIndex Index { get; } = new();
    public ObservableCollection<MapRow> Points { get; } = new();

    [ObservableProperty] private double _latitude;
    [ObservableProperty] private double _longitude;
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _source = "manual";
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private void AddPoint()
    {
        if (Latitude is < -90 or > 90 || Longitude is < -180 or > 180 || double.IsNaN(Latitude) || double.IsNaN(Longitude))
        {
            StatusLine = "Not added: latitude must be between -90 and 90, longitude between -180 and 180.";
            return;
        }
        // A hand-entered point has no event time. Recording "now" would put the moment the
        // examiner typed it into a column that otherwise holds evidence timestamps.
        Add(new MapRow(Latitude, Longitude, null, "", "not recorded",
            Label.Trim(), string.IsNullOrWhiteSpace(Source) ? "manual" : Source.Trim()));
        Latitude = 0; Longitude = 0; Label = "";
        StatusLine = "Point added.";
    }

    [RelayCommand]
    private void Clear()
    {
        Points.Clear();
        Index.Clear();
        StatusLine = "List cleared.";
    }

    private void Add(MapRow row)
    {
        Index.Add(new GeoPoint(row.Latitude, row.Longitude, row.Timestamp, row.Label, row.Source, null));
        Points.Add(row);
    }

    /// <summary>
    /// Walk a folder of images, read the GPS position from EXIF, and add every geo-tagged photo
    /// as a point. Pure C# via MetadataExtractor: no Python, no shell-outs.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task IngestPhotosAsync(CancellationToken ct)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick a folder of photos",
        });
        var root = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(root)) return;

        StatusLine = "Reading photos…";
        var progress = new Progress<(int Scanned, int Found)>(p =>
            StatusLine = $"Reading photos… {p.Scanned:N0} read, {p.Found:N0} with a GPS position");
        try
        {
            var (rows, scanned, capped) = await Task.Run(() => ScanFolderForExifGps(root, progress, ct), ct);
            foreach (var row in rows)
            {
                Add(row);
            }
            StatusLine = rows.Count == 0
                ? $"None of the {scanned:N0} photos under {root} carries a GPS position."
                : $"Added {rows.Count:N0} of {scanned:N0} photos from {root}."
                  + (capped ? $" Stopped at the {MaxPointsPerIngest:N0}-point limit; ingest subfolders separately for the rest." : "");
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Stopped. Nothing from this folder was added.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Could not read {root}: {ex.Message}";
        }
    }

    private static readonly string[] PhotoExtensions = [".jpg", ".jpeg", ".heic", ".heif", ".tiff", ".tif", ".png", ".webp", ".dng"];

    private static (List<MapRow> Rows, int Scanned, bool Capped) ScanFolderForExifGps(
        string root, IProgress<(int, int)> progress, CancellationToken ct)
    {
        var rows = new List<MapRow>();
        var scanned = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        }))
        {
            ct.ThrowIfCancellationRequested();
            if (!PhotoExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
            if (rows.Count >= MaxPointsPerIngest)
            {
                return (rows, scanned, true);
            }
            scanned++;
            if (scanned % 50 == 0)
            {
                progress.Report((scanned, rows.Count));
            }
            try
            {
                var dirs = MetadataExtractor.ImageMetadataReader.ReadMetadata(path);
                var gps = dirs.OfType<MetadataExtractor.Formats.Exif.GpsDirectory>().FirstOrDefault();
                if (gps?.GetGeoLocation() is not { IsZero: false } loc) continue;
                var (ts, display, origin) = PhotoTime(path, dirs, gps);
                rows.Add(new MapRow(loc.Latitude, loc.Longitude, ts, display, origin, System.IO.Path.GetFileName(path), "photo EXIF"));
            }
            catch
            {
                // Unreadable or truncated image: it simply contributes no point.
            }
        }
        return (rows, scanned, false);
    }

    /// <summary>
    /// The best available time for a photo, and where it came from. The GPS fix is UTC by
    /// definition. The camera clock has no time zone unless the camera wrote an offset, so it is
    /// shown as recorded rather than passed off as UTC. File modified time is the last resort
    /// and is labelled as such, because copying a file can change it.
    /// </summary>
    private static (DateTimeOffset? Utc, string Display, string Origin) PhotoTime(
        string path, IReadOnlyList<MetadataExtractor.Directory> dirs, MetadataExtractor.Formats.Exif.GpsDirectory gps)
    {
        const string Format = "yyyy-MM-dd HH:mm:ss";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (gps.TryGetGpsDate(out var gpsUtc))
        {
            var t = new DateTimeOffset(DateTime.SpecifyKind(gpsUtc, DateTimeKind.Utc));
            return (t, t.ToString(Format, inv) + " UTC", "GPS fix");
        }
        var exif = dirs.OfType<MetadataExtractor.Formats.Exif.ExifSubIfdDirectory>().FirstOrDefault();
        if (exif is not null && MetadataExtractor.DirectoryExtensions.TryGetDateTime(
                exif, MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagDateTimeOriginal, out var taken))
        {
            return (null, taken.ToString(Format, inv) + " (no time zone)", "camera clock");
        }
        var modified = new DateTimeOffset(new FileInfo(path).LastWriteTimeUtc, TimeSpan.Zero);
        return (modified, modified.ToString(Format, inv) + " UTC", "file modified time");
    }

    [RelayCommand]
    private async Task ExportAsync(string format, CancellationToken ct)
    {
        if (Points.Count == 0) return;
        if (!string.Equals(format, "kml", StringComparison.OrdinalIgnoreCase))
        {
            var rows = Points.Select(p => (object)new
            {
                p.Latitude, p.Longitude, Time = p.TimeDisplay, p.TimeSource, p.Source, p.Label,
            }).ToList();
            if (await ToolExport.SaveRowsAsync(Id, "locations", format, rows, ct) is { } status) StatusLine = status;
            return;
        }

        var path = await ToolDialog.SaveFileAsync("Export as KML", "locations.kml", "kml");
        if (string.IsNullOrEmpty(path)) return;
        var snapshot = Points.ToList();
        try
        {
            await Task.Run(() => WriteKml(path, snapshot), ct);
            await ActiveCaseContext.LogAsync(CustodyAction.DataExported, new { Tool = Id, Format = "kml", Path = path, Rows = snapshot.Count }, ct);
            StatusLine = $"Exported {snapshot.Count:N0} point{(snapshot.Count == 1 ? "" : "s")} to {path}";
        }
        catch (Exception ex)
        {
            StatusLine = $"Export failed: {ex.Message}";
        }
    }

    /// <summary>KML 2.2, one Placemark per point. XmlWriter does the escaping: labels are evidence-controlled.</summary>
    internal static void WriteKml(string path, IReadOnlyList<MapRow> points)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        using var xml = System.Xml.XmlWriter.Create(path, new System.Xml.XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false) });
        xml.WriteStartDocument();
        xml.WriteStartElement("kml", "http://www.opengis.net/kml/2.2");
        xml.WriteStartElement("Document");
        xml.WriteElementString("name", "Cinder locations");
        foreach (var p in points)
        {
            xml.WriteStartElement("Placemark");
            xml.WriteElementString("name", string.IsNullOrEmpty(p.Label) ? p.Source : p.Label);
            xml.WriteElementString("description", $"{p.TimeDisplay} ({p.TimeSource}); source: {p.Source}".Trim());
            if (p.Timestamp is { } t)
            {
                xml.WriteStartElement("TimeStamp");
                xml.WriteElementString("when", t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", inv));
                xml.WriteEndElement();
            }
            xml.WriteStartElement("Point");
            // KML order is longitude,latitude.
            xml.WriteElementString("coordinates", $"{p.Longitude.ToString("R", inv)},{p.Latitude.ToString("R", inv)},0");
            xml.WriteEndElement();
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }
}

/// <summary>
/// One row of the location list. <see cref="Timestamp"/> is set only when the time is known in
/// UTC; <see cref="TimeDisplay"/> is what the examiner reads and <see cref="TimeSource"/> says
/// which clock it came from.
/// </summary>
public sealed record MapRow(
    double Latitude, double Longitude, DateTimeOffset? Timestamp, string TimeDisplay, string TimeSource, string Label, string Source);

// =====================================================================================
// GRAPH — communication graph builder.
// =====================================================================================

public sealed partial class GraphTool
{
    /// <summary>Hard stop for one ingest, so a huge mail store cannot grow the graph without bound.</summary>
    private const int MaxInteractionsPerIngest = 25_000;

    public CommunicationGraph Graph { get; } = new();
    public ObservableCollection<GraphNode> Nodes { get; } = new();
    public ObservableCollection<GraphEdgeRow> Edges { get; } = new();

    [ObservableProperty] private string _from = "";
    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _source = "manual";
    [ObservableProperty] private string? _subject;
    [ObservableProperty] private string? _statusLine;

    /// <summary>The person picked in the left list; the message list narrows to them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EdgesHeading), nameof(HasSelectedNode))]
    private GraphNode? _selectedNode;

    public bool HasSelectedNode => SelectedNode is not null;

    public string EdgesHeading => SelectedNode is { } n ? $"MESSAGES TO OR FROM {n.Identity.ToUpperInvariant()}" : "ALL MESSAGES";

    partial void OnSelectedNodeChanged(GraphNode? value) => RefreshEdges();

    [RelayCommand]
    private void ShowAll() => SelectedNode = null;

    [RelayCommand]
    private void AddEdge()
    {
        if (string.IsNullOrWhiteSpace(From) || string.IsNullOrWhiteSpace(To))
        {
            StatusLine = "Not added: fill in both who sent it and who received it.";
            return;
        }
        // No timestamp: a hand-entered link has no event time, and "now" would look like one.
        Graph.AddInteraction(From.Trim(), To.Trim(), string.IsNullOrWhiteSpace(Source) ? "manual" : Source.Trim(), null, Subject);
        Refresh();
        From = ""; To = ""; Subject = "";
        StatusLine = "Link added.";
    }

    [RelayCommand]
    private void ClearGraph()
    {
        Graph.Clear();
        Refresh();
        StatusLine = "Graph cleared.";
    }

    /// <summary>
    /// Walk a folder of email files (.eml / .msg / .mbox) and build the graph from the
    /// From / To / Cc headers. Pure C# via MsgReader and an in-house MBOX header scanner.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
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

        StatusLine = "Reading mail…";
        var progress = new Progress<(int Files, int Links)>(p =>
            StatusLine = $"Reading mail… {p.Files:N0} files, {p.Links:N0} sender-recipient links");
        var result = new IngestResult();
        try
        {
            await Task.Run(() => ScanFolderForEmail(root, Graph, result, progress, ct), ct);
            StatusLine = result.Links == 0
                ? $"No readable .eml, .msg or .mbox mail found under {root}."
                : $"Added {result.Links:N0} links from {result.Files:N0} files under {root}."
                  + (result.Unreadable > 0 ? $" {result.Unreadable:N0} files could not be read." : "")
                  + (result.Capped ? $" Stopped at the {MaxInteractionsPerIngest:N0}-link limit; ingest subfolders separately for the rest." : "");
        }
        catch (OperationCanceledException)
        {
            StatusLine = $"Stopped. {result.Links:N0} links read before stopping were kept.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Could not read {root}: {ex.Message}";
        }
        Refresh();
    }

    private sealed class IngestResult
    {
        public int Files;
        public int Links;
        public int Unreadable;
        public bool Capped;
    }

    private static void ScanFolderForEmail(
        string root, CommunicationGraph graph, IngestResult result, IProgress<(int, int)> progress, CancellationToken ct)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        }))
        {
            ct.ThrowIfCancellationRequested();
            if (result.Links >= MaxInteractionsPerIngest)
            {
                result.Capped = true;
                return;
            }
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".msg" or ".eml" or ".mbox")) continue;
            result.Files++;
            try
            {
                switch (ext)
                {
                    case ".msg":
                        IngestMsg(path, graph, ref result.Links);
                        break;
                    case ".eml":
                        IngestEml(path, graph, ref result.Links);
                        break;
                    default:
                        IngestMbox(path, graph, ref result.Links, ct);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                result.Unreadable++;
            }
            progress.Report((result.Files, result.Links));
        }
    }

    private static void IngestMsg(string path, CommunicationGraph graph, ref int added)
    {
        using var msg = new MsgReader.Outlook.Storage.Message(path);
        var from = msg.GetEmailSender(false, false) ?? "";
        var to = msg.GetEmailRecipients(MsgReader.Outlook.RecipientType.To, false, false) ?? "";
        var cc = msg.GetEmailRecipients(MsgReader.Outlook.RecipientType.Cc, false, false) ?? "";
        AddFromHeader(graph, from, JoinRecipients(to, cc), "msg", ToUtc(msg.SentOn), msg.Subject ?? "", ref added);
    }

    private static void IngestEml(string path, CommunicationGraph graph, ref int added)
    {
        using var fs = File.OpenRead(path);
        var msg = MsgReader.Mime.Message.Load(fs);
        var from = msg.Headers.From?.Address ?? "";
        var recipients = (msg.Headers.To ?? []).Concat(msg.Headers.Cc ?? []).Select(t => t.Address);
        // DateSent is DateTime.MinValue when the header is absent or unparseable.
        AddFromHeader(graph, from, string.Join(", ", recipients), "eml", ToUtc(msg.Headers.DateSent), msg.Headers.Subject ?? "", ref added);
    }

    private static string JoinRecipients(string to, string cc) =>
        string.IsNullOrWhiteSpace(cc) ? to : string.IsNullOrWhiteSpace(to) ? cc : to + ", " + cc;

    /// <summary>
    /// A sent time as UTC, or null when the message does not carry one. Never "now": an
    /// invented time in an evidence column is worse than a blank.
    /// </summary>
    private static DateTimeOffset? ToUtc(object? value) => value switch
    {
        DateTimeOffset dto when dto != default => dto.ToUniversalTime(),
        DateTime dt when dt != default => dt.Kind == DateTimeKind.Local
            ? new DateTimeOffset(dt).ToUniversalTime()
            : new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
        _ => null,
    };

    private static void IngestMbox(string path, CommunicationGraph graph, ref int added, CancellationToken ct)
    {
        using var sr = new StreamReader(path);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        string? lastHeader = null;
        var inHeaders = false;
        while ((line = sr.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (added >= MaxInteractionsPerIngest) break;

            if (line.StartsWith("From ", StringComparison.Ordinal))
            {
                FlushMboxHeaders(graph, headers, ref added);
                inHeaders = true;
                lastHeader = null;
                continue;
            }
            if (!inHeaders)
            {
                continue;
            }
            if (line.Length == 0)
            {
                inHeaders = false;
                continue;
            }
            // RFC 5322 folding: a line starting with space or tab continues the previous header.
            // Long To / Cc lists are almost always folded, so ignoring this drops recipients.
            if ((line[0] == ' ' || line[0] == '\t') && lastHeader is not null)
            {
                headers[lastHeader] += " " + line.Trim();
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                lastHeader = line[..colon];
                headers[lastHeader] = line[(colon + 1)..].Trim();
            }
        }
        FlushMboxHeaders(graph, headers, ref added);
    }

    private static void FlushMboxHeaders(CommunicationGraph graph, Dictionary<string, string> headers, ref int added)
    {
        if (headers.Count == 0) return;
        var from = headers.GetValueOrDefault("From", "");
        var to = JoinRecipients(headers.GetValueOrDefault("To", ""), headers.GetValueOrDefault("Cc", ""));
        var subject = headers.GetValueOrDefault("Subject", "");
        DateTimeOffset? ts = headers.TryGetValue("Date", out var d)
            && DateTimeOffset.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
        AddFromHeader(graph, from, to, "mbox", ts, subject, ref added);
        headers.Clear();
    }

    private static void AddFromHeader(CommunicationGraph graph, string from, string to,
                                       string source, DateTimeOffset? ts, string subject, ref int added)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return;
        var sender = ExtractAddress(from);
        // One link per recipient.
        foreach (var recipient in to.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var address = ExtractAddress(recipient);
            if (address.Length == 0) continue;
            graph.AddInteraction(sender, address, source, ts, subject);
            added++;
        }
    }

    internal static string ExtractAddress(string headerValue)
    {
        // Headers come in two shapes: "Name <foo@bar>" and "foo@bar". Pull just the address.
        var s = headerValue.Trim();
        var lt = s.LastIndexOf('<');
        var gt = s.LastIndexOf('>');
        if (lt >= 0 && gt > lt)
        {
            return s[(lt + 1)..gt].Trim();
        }
        return s.Trim('"', ' ');
    }

    private void Refresh()
    {
        var keep = SelectedNode?.Identity;
        Nodes.Clear();
        // Most-connected first: that is the question the examiner is asking of this list.
        foreach (var n in Graph.Nodes.OrderByDescending(n => n.InCount + n.OutCount).ThenBy(n => n.Identity, StringComparer.OrdinalIgnoreCase))
        {
            Nodes.Add(n);
        }
        SelectedNode = keep is null ? null : Nodes.FirstOrDefault(n => string.Equals(n.Identity, keep, StringComparison.OrdinalIgnoreCase));
        RefreshEdges();
    }

    private void RefreshEdges()
    {
        Edges.Clear();
        var who = SelectedNode?.Identity;
        foreach (var e in Graph.Edges)
        {
            if (who is not null
                && !string.Equals(e.From, who, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(e.To, who, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Edges.Add(new GraphEdgeRow(e.From, e.To, e.Source,
                e.Timestamp?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                e.Subject ?? ""));
        }
    }

    [RelayCommand]
    private async Task ExportAsync(string format, CancellationToken ct)
    {
        var rows = Edges.Select(e => (object)new { e.From, e.To, SentUtc = e.SentUtc, e.Source, e.Subject }).ToList();
        if (await ToolExport.SaveRowsAsync(Id, "communications", format, rows, ct) is { } status) StatusLine = status;
    }
}

/// <summary>One sender-to-recipient link as shown in the message list. <see cref="SentUtc"/> is blank when the message carries no date.</summary>
public sealed record GraphEdgeRow(string From, string To, string Source, string SentUtc, string Subject);

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
        var fmt = Enum.Parse<ImageFormat>(Format);
        BytesRead = 0;
        try
        {
            var progress = new Progress<ImageJobProgress>(p => BytesRead = p.BytesRead);
            // In-process for every format: file, block device (\\.\PhysicalDriveN, /dev/sdX)
            // or a container decoded on the fly. Hash-on-read, retry then sector-level
            // fallback on read errors, a JSON acquisition log; raw / VHD / VHDX get a .sha256
            // companion the Verify tool reads, E01 and AFF4 carry their digests inside.
            StatusLine = InProcessImager.IsDevicePath(Source)
                ? "Imaging device — this needs Administrator / root and a write-blocker on the source…"
                : "Imaging…";
            var job = new ImageJob(Source, Output, fmt,
                ExaminerName: Environment.UserName,
                CaseNumber: ActiveCaseContext.Current?.Name,
                Description: EvidenceOpener.IsEwf(Source) ? "Decoded from EWF" : EvidenceOpener.IsAff4(Source) ? "Decoded from AFF4" : null);
            var result = await new InProcessImager().ImageAsync(job, progress, ct);

            StatusLine = $"Done. {result.BytesWritten:N0} bytes → {Output} · MD5 {result.Md5} · SHA-256 {result.Sha256} · {result.BadSectors} bad sector(s)" +
                         (result.BadSectors > 0 ? " — offsets in the .log.json beside the image" + (fmt == ImageFormat.Ewf ? " and in the container's error section" : "") : "");
            await ActiveCaseContext.LogAsync(CustodyAction.EvidenceImaged, new
            {
                Source,
                Output,
                Format = fmt.ToString(),
                result.BytesWritten,
                result.Md5,
                result.Sha1,
                result.Sha256,
                result.BadSectors,
                ElapsedSeconds = result.Elapsed.TotalSeconds,
            }, ct);
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Cancelled.";
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusLine = $"Access denied: {ex.Message}. Imaging a device needs Administrator (Windows) or root (Linux).";
        }
        catch (Exception ex)
        {
            StatusLine = $"Imaging failed: {ex.Message}";
        }
    }
}

public sealed partial class VerifyTool
{
    [ObservableProperty] private string? _imagePath;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _statusLine;

    /// <summary>
    /// Re-hashes an image and compares the result against a reference digest.
    ///
    /// <para>Runs entirely in-process. For an EWF container the reference is the acquisition
    /// hash recorded inside the container itself; for a raw image it's a <c>.sha256</c> /
    /// <c>.md5</c> companion file if one sits beside it.</para>
    ///
    /// <para>The three outcomes are kept distinct on purpose. "Verified" means a reference
    /// existed and matched; "mismatch" means one existed and did not; "unverifiable" means
    /// there was nothing to compare against. Collapsing the third case into a boolean is how a
    /// tool ends up showing a red ✗ for an image that is merely missing a recorded hash — or,
    /// worse, a green ✓ for one that was never actually checked.</para>
    /// </summary>
    [RelayCommand]
    private async Task PickAndRunAsync(CancellationToken ct)
    {
        var path = await ToolDialog.PickFileAsync("Pick image to verify");
        if (string.IsNullOrEmpty(path)) return;

        ImagePath = path;
        ResultText = null;
        StatusLine = "Verifying — re-reading the whole image…";
        try
        {
            if (EvidenceOpener.IsEwf(path))
            {
                await VerifyEwfAsync(path, ct);
            }
            else if (EvidenceOpener.IsAff4(path))
            {
                await VerifyAff4Async(path, ct);
            }
            else
            {
                await VerifyRawAsync(path, ct);
            }
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Cancelled.";
        }
        catch (Exception ex)
        {
            ResultText = null;
            StatusLine = $"Failed: {ex.Message}";
        }
    }

    private async Task VerifyEwfAsync(string path, CancellationToken ct)
    {
        using var reader = Cinder.Imaging.Ewf.EwfReader.Open(path);
        var progress = new Progress<long>(b => StatusLine = $"Verifying — {b:N0} bytes read…");
        var r = await reader.VerifyAsync(progress, ct);

        ResultText = $"""
            Source            : EWF container, {reader.SegmentCount} segment(s)
            Bytes verified    : {r.BytesVerified:N0}
            Damaged chunks    : {r.DamagedChunkCount:N0}

            Recorded MD5      : {r.ExpectedMd5 ?? "— none recorded —"}
            Computed MD5      : {r.ComputedMd5}
            Recorded SHA-1    : {r.ExpectedSha1 ?? "— none recorded —"}
            Computed SHA-1    : {r.ComputedSha1}

            {r.Summary()}
            """;

        StatusLine = r.Md5Match is null && r.Sha1Match is null
            ? "⚠ unverifiable — container records no acquisition hash"
            : r.Verified ? "✓ verified against the acquisition hash" : "✗ VERIFICATION FAILED";

        // A verification is the custody event that matters most — record the digests and the
        // verdict, not just that the tool ran.
        await ActiveCaseContext.LogAsync(CustodyAction.EvidenceHashed, new
        {
            Image = path,
            Container = "ewf",
            reader.SegmentCount,
            r.BytesVerified,
            r.ExpectedMd5,
            r.ComputedMd5,
            r.ExpectedSha1,
            r.ComputedSha1,
            r.DamagedChunkCount,
            Verdict = r.Summary(),
        }, ct);
    }

    /// <summary>
    /// AFF4 records MD5 / SHA-1 / SHA-256 as <c>aff4:hash</c> literals on the image. Re-read
    /// the whole map and compare every digest that was recorded.
    /// </summary>
    private async Task VerifyAff4Async(string path, CancellationToken ct)
    {
        using var reader = Cinder.Imaging.Aff4.Aff4Reader.Open(path);
        await using var stream = reader.OpenStream();
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4 * 1024 * 1024];
        long total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            md5.AppendData(buffer, 0, n);
            sha1.AppendData(buffer, 0, n);
            sha256.AppendData(buffer, 0, n);
            total += n;
            StatusLine = $"Verifying — {total:N0} bytes read…";
        }
        var cMd5 = Convert.ToHexStringLower(md5.GetHashAndReset());
        var cSha1 = Convert.ToHexStringLower(sha1.GetHashAndReset());
        var cSha256 = Convert.ToHexStringLower(sha256.GetHashAndReset());

        bool? md5Ok = reader.RecordedMd5 is null ? null : reader.RecordedMd5 == cMd5;
        bool? sha1Ok = reader.RecordedSha1 is null ? null : reader.RecordedSha1 == cSha1;
        bool? sha256Ok = reader.RecordedSha256 is null ? null : reader.RecordedSha256 == cSha256;
        var any = md5Ok is not null || sha1Ok is not null || sha256Ok is not null;
        var verified = any && md5Ok != false && sha1Ok != false && sha256Ok != false && reader.DamagedChunks.Count == 0;

        ResultText = $"""
            Source            : AFF4 container, image {reader.ImageUrn}
            Bytes verified    : {total:N0}
            Damaged chunks    : {reader.DamagedChunks.Count:N0}

            Recorded MD5      : {reader.RecordedMd5 ?? "— none recorded —"}
            Computed MD5      : {cMd5}
            Recorded SHA-1    : {reader.RecordedSha1 ?? "— none recorded —"}
            Computed SHA-1    : {cSha1}
            Recorded SHA-256  : {reader.RecordedSha256 ?? "— none recorded —"}
            Computed SHA-256  : {cSha256}

            {(!any ? "UNVERIFIABLE: the container records no hash." : verified ? "VERIFIED: every recorded digest reproduces." : "FAILED: a recorded digest does not reproduce, or chunks were damaged.")}
            """;
        StatusLine = !any
            ? "⚠ unverifiable — container records no acquisition hash"
            : verified ? "✓ verified against the acquisition hash" : "✗ VERIFICATION FAILED";

        await ActiveCaseContext.LogAsync(CustodyAction.EvidenceHashed, new
        {
            Image = path,
            Container = "aff4",
            reader.ImageUrn,
            BytesVerified = total,
            reader.RecordedMd5,
            ComputedMd5 = cMd5,
            reader.RecordedSha1,
            ComputedSha1 = cSha1,
            reader.RecordedSha256,
            ComputedSha256 = cSha256,
            DamagedChunks = reader.DamagedChunks.Count,
            Verdict = !any ? "unverifiable" : verified ? "verified" : "failed",
        }, ct);
    }

    private async Task VerifyRawAsync(string path, CancellationToken ct)
    {
        var hashes = new HashService();
        var progress = new Progress<long>(b => StatusLine = $"Verifying — {b:N0} bytes read…");
        var virtualDisk = EvidenceOpener.VirtualDiskKind(path);
        var computed = virtualDisk is null
            ? await hashes.ComputeFileAsync(path, [HashAlgorithmKind.Md5, HashAlgorithmKind.Sha1, HashAlgorithmKind.Sha256], progress, ct)
            : await HashVirtualDiskAsync(path, progress, ct);

        var (reference, referenceSource) = ReadCompanionDigest(path);

        string verdict;
        if (reference is null)
        {
            verdict = "Unverifiable — no .sha256 / .md5 companion file found next to the image. " +
                      "The digests above are this run's measurement, not a check against a reference.";
            StatusLine = "⚠ unverifiable — no reference digest available";
        }
        else
        {
            var match =
                string.Equals(reference, computed.Sha256, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reference, computed.Md5, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reference, computed.Sha1, StringComparison.OrdinalIgnoreCase);

            verdict = match
                ? $"Verified — matches the digest in {referenceSource}."
                : $"VERIFICATION FAILED — {referenceSource} expects {reference}, which matches none of the computed digests.";
            StatusLine = match ? "✓ verified against companion digest" : "✗ VERIFICATION FAILED";
        }

        ResultText = $"""
            Source            : {(virtualDisk is null ? "raw image" : $"{virtualDisk.ToUpperInvariant()} virtual disk (contents hashed, not the container file)")}
            Bytes verified    : {computed.BytesHashed:N0}

            Reference digest  : {reference ?? "— none found —"}{(referenceSource is null ? "" : $"  ({referenceSource})")}
            Computed MD5      : {computed.Md5}
            Computed SHA-1    : {computed.Sha1}
            Computed SHA-256  : {computed.Sha256}

            {verdict}
            """;

        await ActiveCaseContext.LogAsync(CustodyAction.EvidenceHashed, new
        {
            Image = path,
            Container = "raw",
            computed.BytesHashed,
            computed.Md5,
            computed.Sha1,
            computed.Sha256,
            Reference = reference,
            ReferenceSource = referenceSource,
            Verdict = verdict,
        }, ct);
    }

    /// <summary>
    /// Looks for a digest recorded alongside the image — either <c>image.ext.sha256</c> style
    /// companions or a <c>SHA256SUMS</c>-style line naming the file. Returns the hex digest and
    /// the file it came from, or (null, null) when there is nothing to compare against.
    /// </summary>
    /// <summary>The digest of a VHD / VHDX is over the disk contents — what the imager hashed and what a mount exposes.</summary>
    private static async Task<MultiHashResult> HashVirtualDiskAsync(string path, IProgress<long> progress, CancellationToken ct)
    {
        await using var s = EvidenceOpener.Open(path);
        return await new HashService().ComputeAsync(s, [HashAlgorithmKind.Md5, HashAlgorithmKind.Sha1, HashAlgorithmKind.Sha256], progress, ct);
    }

    private static (string? Digest, string? Source) ReadCompanionDigest(string imagePath)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(imagePath)) ?? ".";
        var name = System.IO.Path.GetFileName(imagePath);

        foreach (var suffix in new[] { ".sha256", ".sha1", ".md5" })
        {
            foreach (var candidate in new[] { imagePath + suffix, System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(imagePath) + suffix) })
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }
                var first = File.ReadLines(candidate).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(first))
                {
                    continue;
                }
                // Both "<digest>" and "<digest>  <filename>" forms.
                var token = first.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (IsHexDigest(token))
                {
                    return (token!.ToLowerInvariant(), System.IO.Path.GetFileName(candidate));
                }
            }
        }

        foreach (var sums in new[] { "SHA256SUMS", "SHA256SUMS.txt", "MD5SUMS", "MD5SUMS.txt" })
        {
            var candidate = System.IO.Path.Combine(dir, sums);
            if (!File.Exists(candidate))
            {
                continue;
            }
            foreach (var line in File.ReadLines(candidate))
            {
                var parts = line.Split([' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 &&
                    IsHexDigest(parts[0]) &&
                    string.Equals(System.IO.Path.GetFileName(parts[^1]), name, StringComparison.OrdinalIgnoreCase))
                {
                    return (parts[0].ToLowerInvariant(), sums);
                }
            }
        }

        return (null, null);
    }

    private static bool IsHexDigest(string? s) =>
        s is { Length: 32 or 40 or 64 } && s.All(Uri.IsHexDigit);
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
            await ActiveCaseContext.LogAsync(CustodyAction.EvidenceMounted, new
            {
                Image = path,
                handle.MountPoint,
                ReadOnly = true,
            }, ct);
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

    /// <summary>Every target is written in-process; the source can be raw, E01, AFF4, VHD or VHDX.</summary>
    public IReadOnlyList<string> Formats { get; } = ["raw", "E01", "AFF4", "VHD", "VHDX"];

    [ObservableProperty] private long _bytesConverted;

    private ImageFormat TargetFormat => Format switch
    {
        "E01" => ImageFormat.Ewf,
        "AFF4" => ImageFormat.Aff4,
        "VHD" => ImageFormat.Vhd,
        "VHDX" => ImageFormat.Vhdx,
        _ => ImageFormat.Raw,
    };

    [RelayCommand]
    private async Task PickSourceAsync(CancellationToken ct)
    {
        var p = await ToolDialog.PickFileAsync("Source image (raw, .E01 chain, .aff4, .vhd, .vhdx)");
        if (!string.IsNullOrEmpty(p)) Source = p;
    }

    [RelayCommand]
    private async Task PickOutputAsync(CancellationToken ct)
    {
        var ext = TargetFormat.DefaultExtension();
        var p = await ToolDialog.SaveFileAsync("Output", "out" + ext, ext.TrimStart('.'));
        if (!string.IsNullOrEmpty(p)) Output = p;
    }

    /// <summary>
    /// Reads the source through whichever reader understands it, writes the target through
    /// <see cref="InProcessImager"/> (hashing as it streams), then re-reads the finished output
    /// and checks it hashes back to the same digest. When the source was a container with a
    /// recorded acquisition hash, that is compared too — so the conversion is also a
    /// verification of both ends.
    /// </summary>
    [RelayCommand]
    private async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Source) || string.IsNullOrEmpty(Output))
        {
            StatusLine = "Pick a source and an output path.";
            return;
        }
        BytesConverted = 0;
        StatusLine = $"Converting to {Format} — hashing as it streams, then re-reading the output to verify…";
        try
        {
            var progress = new Progress<ImageJobProgress>(p => BytesConverted = p.BytesRead);
            var r = await ImageConverter.ConvertAsync(Source, Output, TargetFormat, Environment.UserName, progress: progress, ct: ct);

            var outputVerdict = r.MatchesRecorded switch
            {
                true => "✓ output re-read and hashes back to what was written",
                false => $"✗ output re-read does NOT reproduce the digest ({r.DamagedChunks} damaged chunk(s)) — do not use it",
                null => "⚠ output could not be re-read for verification",
            };
            var sourceVerdict = r.SourceMatchesRecorded switch
            {
                true => "; source matched its recorded acquisition hash",
                false => "; ✗ source did NOT match its recorded acquisition hash",
                null => r.RecordedMd5 is null ? "" : "",
            };
            StatusLine = $"Done. {r.Image.BytesWritten:N0} bytes → {Output} · MD5 {r.Image.Md5} · SHA-256 {r.Image.Sha256} · {outputVerdict}{sourceVerdict}";
            await ActiveCaseContext.LogAsync(CustodyAction.EvidenceImaged, new
            {
                Operation = "convert",
                Source,
                Output,
                Format = TargetFormat.ToString(),
                r.Image.BytesWritten,
                r.Image.Md5,
                r.Image.Sha1,
                r.Image.Sha256,
                r.Image.BadSectors,
                OutputVerified = r.MatchesRecorded,
                SourceRecordedMd5 = r.RecordedMd5,
                SourceRecordedSha1 = r.RecordedSha1,
                SourceVerified = r.SourceMatchesRecorded,
            }, ct);
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Conversion failed: {ex.Message}";
        }
    }
}

public sealed partial class ShadowCopyTool
{
    [ObservableProperty] private string? _statusLine;

    /// <summary>"Nothing listed yet" and "listed, and there are none" are different answers.</summary>
    [ObservableProperty] private string _emptyTitle = "Snapshots on this computer";

    public ObservableCollection<Cinder.Native.ShadowCopy> Snapshots { get; } = new();

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        Snapshots.Clear();
        StatusLine = "Listing snapshots…";
        try
        {
            // Enumeration shells out to the OS snapshot tools; keep it off the UI thread.
            var found = await Task.Run(() => ShadowCopyService.ForCurrentPlatform().Enumerate().ToList(), ct);
            foreach (var s in found)
            {
                Snapshots.Add(s);
            }
            EmptyTitle = "No snapshots found";
            StatusLine = found.Count == 0
                ? "This computer has no snapshots, or Cinder is not running as Administrator / root."
                : $"{found.Count:N0} snapshot{(found.Count == 1 ? "" : "s")} found.";
        }
        catch (Exception ex)
        {
            EmptyTitle = "Could not list snapshots";
            StatusLine = $"Could not list snapshots: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportAsync(string format, CancellationToken ct)
    {
        var rows = Snapshots.Select(s => (object)new
        {
            Snapshot = s.Id, Volume = s.Origin,
            CreatedUtc = s.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), s.Notes,
        }).ToList();
        if (await ToolExport.SaveRowsAsync(Id, "snapshots", format, rows, ct) is { } status) StatusLine = status;
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
            await using var fs = Cinder.Imaging.EvidenceOpener.Open(Source);
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
    private readonly Cinder.App.Services.SettingsStore _settings = new();

    [ObservableProperty] private string _provider = "google-drive";
    [ObservableProperty] private string? _statusLine;
    [ObservableProperty] private string? _authorizeUrl;
    [ObservableProperty] private string? _redirectUri;
    [ObservableProperty] private string? _instructions;
    public IReadOnlyList<string> Providers { get; } = ["google-drive", "onedrive", "dropbox"];

    public CloudPullTool() => RefreshAuthorizeUrl();

    partial void OnProviderChanged(string value) => RefreshAuthorizeUrl();

    /// <summary>
    /// Computes a provider-specific authorize URL with PKCE so the user can paste it into
    /// their browser, complete consent, and bring back the auth code. We never embed a
    /// browser ourselves — that crosses the SSO trust boundary the user expects. Instead we
    /// surface the URL + redirect_uri + step-by-step instructions and ask the user to paste
    /// the resulting code back in.
    /// </summary>
    private void RefreshAuthorizeUrl()
    {
        var settings = _settings.Load();
        var clientId = settings.CloudClientIds.GetValueOrDefault(Provider, "");
        if (string.IsNullOrEmpty(clientId))
        {
            AuthorizeUrl = null;
            RedirectUri = null;
            Instructions = $"Set the OAuth client_id for {Provider} in Settings → Cloud first.\n" +
                           "See docs/cloud-setup.md for how to create one in your own Google / Microsoft / Dropbox dev console — Cinder doesn't ship shared client_ids on purpose, so each examiner's cloud pulls are auditable as their own account.";
            return;
        }

        // PKCE — generate a fresh verifier + challenge per click. Verifier kept in memory so
        // the eventual token exchange (TODO: paste-code flow) can use it.
        var verifier = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // OOB redirect — same approach Google Cloud SDK used for years: redirect to a page
        // whose URL contains the code in its title bar / window title. Modern providers also
        // accept http://127.0.0.1/<port> for desktop apps; we surface the localhost option
        // alongside the OOB for cases where it's allowed.
        RedirectUri = "http://127.0.0.1:48127/oauth/callback";

        var (authBase, scope) = Provider switch
        {
            "google-drive" => ("https://accounts.google.com/o/oauth2/v2/auth",
                "https://www.googleapis.com/auth/drive.readonly"),
            "onedrive" => ("https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
                "files.read.all offline_access"),
            "dropbox" => ("https://www.dropbox.com/oauth2/authorize",
                "files.metadata.read files.content.read"),
            _ => ("", ""),
        };
        if (authBase.Length == 0) return;

        var q = new System.Collections.Generic.Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["access_type"] = "offline",
        };
        var query = string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        AuthorizeUrl = $"{authBase}?{query}";
        Instructions =
            "1. Click 'Copy URL', paste it in your browser, complete consent.\n" +
            "2. After consent the browser redirects to 127.0.0.1:48127 — the page won't load\n" +
            "   (no local listener yet), but the URL bar shows ?code=<long-string>.\n" +
            "3. Paste that code back into Cinder via the 'Submit code' button (next pass).\n" +
            "Token exchange + actual file pull will land in v0.3.";
        StatusLine = "Authorize URL ready.";
    }

    [RelayCommand]
    private void CopyAuthorizeUrl()
    {
        if (string.IsNullOrEmpty(AuthorizeUrl))
        {
            StatusLine = "No authorize URL — set the client_id in Settings → Cloud first.";
            return;
        }
        try
        {
            var top = (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            top?.Clipboard?.SetTextAsync(AuthorizeUrl);
            StatusLine = "Copied — paste into your browser.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Copy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Connect()
    {
        // Legacy command kept for the old button label; routes through Refresh.
        RefreshAuthorizeUrl();
    }
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
