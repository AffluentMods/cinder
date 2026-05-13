using System.Reflection;
using Cinder.Artifacts;

namespace Cinder.Plugins;

/// <summary>Cinder plugin contract. Plugins are loaded from a per-user directory and run in
/// the main process; isolation requested via <see cref="LoadIsolated"/> spawns a sidecar.</summary>
public interface IPlugin
{
    string Id { get; }
    string DisplayName { get; }
    string Author { get; }
    string Version { get; }

    void Register(IPluginContext context);
}

public interface IPluginContext
{
    /// <summary>Register a parser that emits <see cref="IArtifact"/> records.</summary>
    void RegisterParser(string id, IParserExtension extension);

    /// <summary>Register a viewer for an artifact source.</summary>
    void RegisterViewer(string artifactSource, IViewerExtension extension);

    /// <summary>Register a command palette action.</summary>
    void RegisterCommand(string id, string title, Func<CancellationToken, Task> invoke);
}

public interface IParserExtension
{
    IAsyncEnumerable<IArtifact> ParseAsync(string targetPath, CancellationToken ct);
}

public interface IViewerExtension
{
    /// <summary>Returns an Avalonia control type that knows how to render the source's artifacts.</summary>
    Type ControlType { get; }
}

public sealed class PluginLoader
{
    /// <summary>
    /// Sentinel filename inside the plugin directory that opts the user in to loading plugins.
    /// The user must create this file (with an explicit click in the Plugins tool, or by hand)
    /// before any plugin DLLs are loaded. Without it the loader returns an empty list. This is a
    /// defense-in-depth measure against malware that drops a .dll into the plugin folder and
    /// expects it to auto-load on next Cinder launch.
    /// </summary>
    public const string TrustSentinelFile = ".cinder-trusted";

    /// <summary>
    /// Per-plugin trust manifest. Each line is a SHA-256 hex digest of a plugin DLL the user has
    /// explicitly approved. The loader skips any DLL whose hash is not in this list.
    /// </summary>
    public const string TrustManifestFile = ".cinder-plugins.sha256";

    public IReadOnlyList<PluginLoadResult> LoadFromDirectory(string directory)
    {
        Directory.CreateDirectory(directory);

        // SECURITY: do nothing unless the user has explicitly opted in.
        if (!File.Exists(Path.Combine(directory, TrustSentinelFile)))
        {
            return Array.Empty<PluginLoadResult>();
        }

        var trustedHashes = LoadTrustedHashes(directory);
        var results = new List<PluginLoadResult>();

        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll"))
        {
            var fileName = Path.GetFileName(dll);
            string hash;
            try
            {
                hash = ComputeSha256Hex(dll);
            }
            catch (Exception ex)
            {
                results.Add(PluginLoadResult.Failed(fileName, $"Could not hash plugin: {ex.Message}"));
                continue;
            }

            // SECURITY: only load DLLs whose hash the user has explicitly trusted. The trust
            // manifest is plain text the user maintains via the Plugins UI.
            if (!trustedHashes.Contains(hash))
            {
                results.Add(PluginLoadResult.Untrusted(fileName, hash));
                continue;
            }

            try
            {
                var asm = Assembly.LoadFrom(dll);
                foreach (var type in asm.GetExportedTypes()
                    .Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract))
                {
                    if (Activator.CreateInstance(type) is IPlugin plugin)
                    {
                        results.Add(PluginLoadResult.Loaded(fileName, hash, plugin));
                    }
                }
            }
            catch (Exception ex)
            {
                results.Add(PluginLoadResult.Failed(fileName, ex.Message));
            }
        }
        return results;
    }

    private static HashSet<string> LoadTrustedHashes(string directory)
    {
        var path = Path.Combine(directory, TrustManifestFile);
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            // Accept either "hash" alone or "hash  filename" (sha256sum format).
            var hash = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];
            if (hash.Length == 64)
            {
                hashes.Add(hash);
            }
        }
        return hashes;
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        var digest = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexStringLower(digest);
    }
}

/// <summary>
/// Result of an individual plugin DLL load attempt. Status drives UI surfacing: "loaded" goes
/// green, "untrusted" prompts the user to approve the hash, "failed" shows the error.
/// </summary>
public sealed record PluginLoadResult(
    string FileName,
    string? Sha256,
    IPlugin? Plugin,
    string Status,
    string? Error)
{
    public static PluginLoadResult Loaded(string fileName, string hash, IPlugin plugin)
        => new(fileName, hash, plugin, Status: "loaded", Error: null);

    public static PluginLoadResult Untrusted(string fileName, string hash)
        => new(fileName, hash, Plugin: null, Status: "untrusted", Error: null);

    public static PluginLoadResult Failed(string fileName, string error)
        => new(fileName, Sha256: null, Plugin: null, Status: "failed", Error: error);
}

public sealed class PluginContext : IPluginContext
{
    private readonly Dictionary<string, IParserExtension> _parsers = new();
    private readonly Dictionary<string, IViewerExtension> _viewers = new();
    private readonly Dictionary<string, (string Title, Func<CancellationToken, Task> Invoke)> _commands = new();

    public IReadOnlyDictionary<string, IParserExtension> Parsers => _parsers;
    public IReadOnlyDictionary<string, IViewerExtension> Viewers => _viewers;
    public IReadOnlyDictionary<string, (string Title, Func<CancellationToken, Task> Invoke)> Commands => _commands;

    public void RegisterParser(string id, IParserExtension extension) => _parsers[id] = extension;
    public void RegisterViewer(string artifactSource, IViewerExtension extension) => _viewers[artifactSource] = extension;
    public void RegisterCommand(string id, string title, Func<CancellationToken, Task> invoke) => _commands[id] = (title, invoke);
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class LoadIsolated : Attribute { }
