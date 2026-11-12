using System.Reflection;
using Cinder.Artifacts;

namespace Cinder.Plugins;

/// <summary>Cinder plugin contract. Plugins are loaded from a directory the examiner chooses and
/// run in the main process, each in its own <c>AssemblyLoadContext</c> (which separates their
/// dependencies; it is not a sandbox). <see cref="LoadIsolated"/> is declared for a future
/// out-of-process mode and is not enforced yet.</summary>
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

/// <summary>
/// Loads third-party plugin assemblies, behind three gates:
/// <list type="number">
/// <item>the examiner has switched plugins on (<see cref="PluginTrustStore.PluginsEnabled"/>);</item>
/// <item>the SHA-256 of the assembly's bytes is one they approved
/// (<see cref="PluginTrustStore.Trust"/>);</item>
/// <item>so is the SHA-256 of every assembly it loads from the plugin folder.</item>
/// </list>
/// Both records live in per-user storage (<see cref="PluginTrustStore"/>), not in the plugin
/// folder: a folder cannot approve its own contents.
/// </summary>
public sealed class PluginLoader
{
    /// <summary>
    /// Name of the opt-in file earlier builds looked for <em>inside</em> the plugin directory.
    /// It is no longer read: whoever can put a DLL in a folder can put this next to it. The
    /// opt-in is <see cref="PluginTrustStore.SetPluginsEnabled"/>. Kept so callers can detect
    /// and explain a leftover file.
    /// </summary>
    public const string TrustSentinelFile = ".cinder-trusted";

    /// <summary>
    /// Name of the hash manifest earlier builds read from inside the plugin directory. No
    /// longer read, for the same reason; approvals are made with
    /// <see cref="PluginTrustStore.Trust"/>. Entries are deliberately not migrated — a manifest
    /// found in the folder is exactly what cannot be believed.
    /// </summary>
    public const string TrustManifestFile = ".cinder-plugins.sha256";

    // A plugin assembly is read into memory to be hashed and loaded from the same bytes.
    private const long MaxAssemblyBytes = 512L * 1024 * 1024;

    /// <summary>The examiner's opt-in and approved hashes that this loader consults.</summary>
    public PluginTrustStore TrustStore { get; }

    /// <summary>A loader backed by the per-user store at <see cref="PluginTrustStore.DefaultPath"/>.</summary>
    public PluginLoader() : this(PluginTrustStore.Default)
    {
    }

    public PluginLoader(PluginTrustStore trustStore)
    {
        TrustStore = trustStore ?? throw new ArgumentNullException(nameof(trustStore));
    }

    /// <summary>
    /// True when <paramref name="directory"/> still contains the trust files earlier builds
    /// honoured. They have no effect now; the app can use this to tell the examiner why plugins
    /// that used to load are listed as untrusted.
    /// </summary>
    public static bool HasLegacyTrustFiles(string directory) =>
        File.Exists(Path.Combine(directory, TrustSentinelFile)) || File.Exists(Path.Combine(directory, TrustManifestFile));

    /// <summary>
    /// Examines every <c>*.dll</c> directly inside <paramref name="directory"/> and returns one
    /// result per file (or per plugin type found in a loaded file):
    /// <c>untrusted</c> — not approved, with its hash so it can be;
    /// <c>loaded</c> — approved, loaded and instantiated;
    /// <c>failed</c> — approved but could not be loaded, including because one of its
    /// dependencies is not approved.
    /// Returns nothing at all while plugins are switched off.
    /// </summary>
    public IReadOnlyList<PluginLoadResult> LoadFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);

        // SECURITY: do nothing unless the examiner has explicitly opted in — in their own
        // profile, not by way of a file in this folder.
        if (!TrustStore.PluginsEnabled)
        {
            return Array.Empty<PluginLoadResult>();
        }

        var trustedHashes = TrustStore.SnapshotTrustedHashes();
        var results = new List<PluginLoadResult>();

        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(dll);
            byte[] bytes;
            string hash;
            AuthenticodeInfo? signature;
            try
            {
                // One handle, opened without write or delete sharing, for the read, the hash
                // and the signature check: on Windows the file cannot be replaced underneath
                // them. What is loaded afterwards is `bytes`, not the file.
                using var handle = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (handle.Length > MaxAssemblyBytes)
                {
                    results.Add(PluginLoadResult.Failed(fileName, $"File is {handle.Length:N0} bytes; plugin assemblies over {MaxAssemblyBytes:N0} bytes are not loaded."));
                    continue;
                }
                bytes = new byte[handle.Length];
                handle.ReadExactly(bytes);
                hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                signature = AuthenticodeVerifier.Inspect(dll);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(PluginLoadResult.Failed(fileName, $"Could not hash plugin: {ex.Message}"));
                continue;
            }

            // SECURITY: only load assemblies whose hash the examiner has approved. The signer
            // is shown either way — it is part of what they decide on — but a signature is
            // never a substitute for the approval.
            if (!trustedHashes.Contains(hash))
            {
                results.Add(PluginLoadResult.Untrusted(fileName, hash) with { Signature = signature });
                continue;
            }

            try
            {
                var context = new PluginAssemblyLoadContext(directory, fileName, trustedHashes);
                var asm = context.LoadPlugin(bytes);
                foreach (var type in asm.GetExportedTypes()
                    .Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract))
                {
                    if (Activator.CreateInstance(type) is IPlugin plugin)
                    {
                        results.Add(PluginLoadResult.Loaded(fileName, hash, plugin, signature));
                    }
                }
            }
            catch (Exception ex)
            {
                // The hash gate was passed, so keep the hash on the failure row — the examiner
                // needs to see WHICH dll failed, and the hash is its identity. A plugin
                // constructor that throws surfaces its own exception, not the reflection wrapper.
                var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                results.Add(PluginLoadResult.Failed(fileName, cause.Message) with { Sha256 = hash, Signature = signature });
            }
        }
        return results;
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
    string? Error,
    AuthenticodeInfo? Signature = null)
{
    public static PluginLoadResult Loaded(string fileName, string hash, IPlugin plugin, AuthenticodeInfo? signature = null)
        => new(fileName, hash, plugin, Status: "loaded", Error: null, Signature: signature);

    public static PluginLoadResult Untrusted(string fileName, string hash)
        => new(fileName, hash, Plugin: null, Status: "untrusted", Error: null);

    public static PluginLoadResult Failed(string fileName, string error)
        => new(fileName, Sha256: null, Plugin: null, Status: "failed", Error: error);

    /// <summary>
    /// Publisher string for the Plugins UI. A name is followed by "✓" only when Windows
    /// verified the signature against the file as it is now and the chain is trusted. A
    /// certificate that is merely present — copied from another binary, or left on a file
    /// modified after signing — is named but marked as not verifying.
    /// </summary>
    public string SignatureDisplay => Signature is null
        ? "unsigned"
        : !Signature.FileIntact
            ? $"{ExtractCommonName(Signature.Subject)} (signature does not match file)"
            : Signature.ChainValid
                ? $"{ExtractCommonName(Signature.Subject)} ✓"
                : $"{ExtractCommonName(Signature.Subject)} (untrusted chain)";

    private static string ExtractCommonName(string subject)
    {
        var idx = subject.IndexOf("CN=", StringComparison.Ordinal);
        if (idx < 0) return subject;
        var rest = subject[(idx + 3)..];
        var comma = rest.IndexOf(',');
        return comma > 0 ? rest[..comma].Trim() : rest.Trim();
    }
}

/// <summary>
/// Authenticode signature data for a plugin DLL, from <see cref="AuthenticodeVerifier"/>. It is
/// shown to the examiner; it is never what allows a plugin to load — that is the approved hash.
/// </summary>
/// <param name="Subject">Signer certificate subject.</param>
/// <param name="Issuer">Signer certificate issuer.</param>
/// <param name="NotAfter">Signer certificate expiry.</param>
/// <param name="ChainValid">True only when <c>WinVerifyTrust</c> accepted the file: the
/// signature covers the file's current bytes and chains to a root this machine trusts. (It
/// used to mean only that the certificate chained, which is true of a certificate table pasted
/// onto any file.)</param>
/// <param name="Thumbprint">Signer certificate thumbprint.</param>
/// <param name="FileIntact">False when the signature is present but does not match the file's
/// contents — the file was changed after signing, or the signature belongs to another file.</param>
public sealed record AuthenticodeInfo(
    string Subject,
    string Issuer,
    DateTime NotAfter,
    bool ChainValid,
    string Thumbprint,
    bool FileIntact = true);

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
