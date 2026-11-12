using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cinder.Plugins;

/// <summary>
/// Where the examiner's plugin decisions live: whether plugins may load at all, and the SHA-256
/// of each assembly they have approved.
///
/// <para><b>Why this is not in the plugin directory.</b> It used to be — a
/// <c>.cinder-trusted</c> sentinel and a <c>.cinder-plugins.sha256</c> manifest next to the
/// DLLs. But the plugin directory is the one place an attacker is assumed able to write (that
/// is the threat the gate exists for), and it is also something examiners hand each other:
/// whoever supplies the DLL can supply the two files that approve it. A folder must not be able
/// to vouch for its own contents. The record now lives under the user's local application-data
/// folder (<see cref="DefaultPath"/>), which a plugin folder — downloaded, unzipped, on a share —
/// cannot reach by being opened.</para>
///
/// <para>This is not a defence against code already running as the user; nothing stored in the
/// user's profile can be. It removes the cases where merely <em>choosing a folder</em> ran
/// someone else's code.</para>
///
/// <para>Every call reads the file afresh and every change is written atomically, so two
/// windows — or the app and a test — never work from a stale copy. A file that cannot be read
/// or parsed means "nothing is trusted, plugins are off".</para>
/// </summary>
public sealed class PluginTrustStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// <c>%LOCALAPPDATA%\Cinder\plugin-trust.json</c> on Windows,
    /// <c>~/.local/share/Cinder/plugin-trust.json</c> on Linux.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cinder", "plugin-trust.json");

    /// <summary>The store the app uses: the per-user file at <see cref="DefaultPath"/>.</summary>
    public static PluginTrustStore Default => new(DefaultPath);

    public string FilePath { get; }

    public PluginTrustStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    /// <summary>
    /// The examiner's opt-in. While false, <see cref="PluginLoader.LoadFromDirectory"/> does
    /// nothing at all — it does not hash, inspect or list what is in the folder.
    /// </summary>
    public bool PluginsEnabled => Read().Enabled;

    /// <summary>Records the examiner's explicit choice to allow (or stop allowing) plugins.</summary>
    public void SetPluginsEnabled(bool enabled)
    {
        var state = Read();
        state.Enabled = enabled;
        Write(state);
    }

    /// <summary>The assemblies the examiner has approved, oldest first.</summary>
    public IReadOnlyList<TrustedPlugin> TrustedPlugins =>
        [.. Read().Trusted.Select(t => new TrustedPlugin(t.Sha256, t.FileName, t.TrustedUtc))];

    /// <summary>True when <paramref name="sha256"/> (hex, any case) has been approved.</summary>
    public bool IsTrusted(string? sha256) =>
        TryNormalize(sha256, out var hash) && Read().Trusted.Any(t => t.Sha256 == hash);

    /// <summary>
    /// Approves one assembly by the SHA-256 of its bytes — the value a
    /// <see cref="PluginLoadResult"/> with status <c>untrusted</c> carries in
    /// <see cref="PluginLoadResult.Sha256"/>. Approval is of those exact bytes: a rebuilt or
    /// modified file has a different hash and is untrusted again. A plugin's dependencies are
    /// approved the same way, one hash each.
    /// </summary>
    /// <param name="sha256">64 hex characters.</param>
    /// <param name="fileName">Remembered for display only; it plays no part in the check.</param>
    /// <exception cref="ArgumentException"><paramref name="sha256"/> is not a SHA-256 hex digest.</exception>
    public void Trust(string sha256, string? fileName = null)
    {
        if (!TryNormalize(sha256, out var hash))
        {
            throw new ArgumentException("Expected a SHA-256 digest as 64 hexadecimal characters.", nameof(sha256));
        }
        var state = Read();
        if (state.Trusted.Any(t => t.Sha256 == hash))
        {
            return;
        }
        state.Trusted.Add(new Entry { Sha256 = hash, FileName = fileName is null ? null : Path.GetFileName(fileName), TrustedUtc = DateTimeOffset.UtcNow });
        Write(state);
    }

    /// <summary>Withdraws an approval. Returns false if the hash was not trusted.</summary>
    public bool Revoke(string sha256)
    {
        if (!TryNormalize(sha256, out var hash))
        {
            return false;
        }
        var state = Read();
        if (state.Trusted.RemoveAll(t => t.Sha256 == hash) == 0)
        {
            return false;
        }
        Write(state);
        return true;
    }

    /// <summary>A snapshot of the approved hashes, for one load pass.</summary>
    internal HashSet<string> SnapshotTrustedHashes() =>
        Read().Trusted.Select(t => t.Sha256).ToHashSet(StringComparer.Ordinal);

    private static bool TryNormalize(string? sha256, out string normalized)
    {
        normalized = "";
        var trimmed = sha256?.Trim();
        if (trimmed is not { Length: 64 } || !trimmed.All(char.IsAsciiHexDigit))
        {
            return false;
        }
        normalized = trimmed.ToLowerInvariant();
        return true;
    }

    private State Read()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new State();
            }
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath), Json) ?? new State();
            state.Trusted = [.. (state.Trusted ?? []).Where(t => t is not null && TryNormalize(t.Sha256, out _))];
            foreach (var t in state.Trusted)
            {
                t.Sha256 = t.Sha256.ToLowerInvariant();
            }
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Fail closed: an unreadable record trusts nothing and enables nothing.
            return new State();
        }
    }

    private void Write(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    private sealed class State
    {
        public bool Enabled { get; set; }
        public List<Entry> Trusted { get; set; } = [];
    }

    private sealed class Entry
    {
        public string Sha256 { get; set; } = "";
        public string? FileName { get; set; }
        public DateTimeOffset TrustedUtc { get; set; }
    }
}

/// <summary>One approved assembly, as recorded in the <see cref="PluginTrustStore"/>.</summary>
public sealed record TrustedPlugin(string Sha256, string? FileName, DateTimeOffset TrustedUtc);
