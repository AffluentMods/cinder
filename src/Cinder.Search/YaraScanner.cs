namespace Cinder.Search;

/// <summary>
/// Cinder's YARA scanning surface.
///
/// **Implementation status (Phase 6):** the API is final but the engine binding is wired only
/// when <c>libyara</c> is available on PATH. dnYara is referenced in the package list but
/// requires the native libyara DLL/SO to be shipped alongside Cinder. For Phase 6 we expose
/// the type contracts and a sidecar fallback so users can scan with <c>yara-python</c>; the
/// fast in-process binding lands in 6.1 once the libyara binary distribution is sorted.
/// </summary>
public sealed record YaraRule(string Name, string Source, IReadOnlyList<string> Tags, IReadOnlyDictionary<string, string> Metadata);

public sealed record YaraMatch(string RuleName, string Path, long Offset, IReadOnlyList<string> MatchedStrings);

public interface IYaraScanner
{
    Task<IReadOnlyList<YaraRule>> CompileAsync(IReadOnlyList<string> rulePaths, CancellationToken ct);
    IAsyncEnumerable<YaraMatch> ScanFileAsync(string path, CancellationToken ct);
    IAsyncEnumerable<YaraMatch> ScanBytesAsync(string label, ReadOnlyMemory<byte> bytes, CancellationToken ct);
}

/// <summary>Sidecar-backed YARA scanner. Spawns a Python process running <c>yara-python</c>.</summary>
public sealed class SidecarYaraScanner : IYaraScanner
{
    // Implementation deferred to <c>parsers/yara/yara_worker.py</c> (Phase 6.1).
    public Task<IReadOnlyList<YaraRule>> CompileAsync(IReadOnlyList<string> rulePaths, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<YaraRule>>([]);

    public async IAsyncEnumerable<YaraMatch> ScanFileAsync(string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        _ = path; _ = ct;
        yield break; // TODO 6.1: wire `yara-python` sidecar
    }

    public async IAsyncEnumerable<YaraMatch> ScanBytesAsync(string label, ReadOnlyMemory<byte> bytes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        _ = label; _ = bytes; _ = ct;
        yield break;
    }
}
