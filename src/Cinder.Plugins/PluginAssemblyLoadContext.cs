using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace Cinder.Plugins;

/// <summary>
/// The load context one plugin runs in. Its job is to make "the examiner approved this hash"
/// true of <em>every</em> assembly the plugin brings into the process, not only the one file
/// they clicked on.
///
/// <para><c>Assembly.LoadFrom</c>, which this replaces, resolves a plugin's references from the
/// plugin's own folder without asking anyone: put a different <c>Helper.dll</c> next to an
/// approved <c>Plugin.dll</c> and it ran. Here every assembly — managed or native — that is
/// taken from the plugin directory is read once, hashed, and loaded from those same bytes only
/// if that hash is trusted. There is no window between the check and the load in which the
/// file could be swapped.</para>
///
/// <para>Assemblies the host already provides (the framework, <c>Cinder.Plugins</c> itself and
/// the other Cinder contract assemblies) always come from the host, never from the folder, so a
/// plugin's <see cref="IPlugin"/> is the host's <see cref="IPlugin"/> and a planted
/// <c>System.Text.Json.dll</c> is simply ignored.</para>
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly string _directory;
    private readonly string _pluginFile;
    private readonly IReadOnlySet<string> _trustedHashes;
    private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public PluginAssemblyLoadContext(string directory, string pluginFile, IReadOnlySet<string> trustedHashes)
        : base(name: "cinder-plugin:" + pluginFile, isCollectible: false)
    {
        _directory = directory;
        _pluginFile = pluginFile;
        _trustedHashes = trustedHashes;
    }

    /// <summary>
    /// Loads the plugin's own assembly from bytes the caller has already hashed and found
    /// trusted, then walks its references and loads — and therefore checks — every one that
    /// would come from the plugin directory. A plugin with an unapproved dependency fails here,
    /// before any of its code runs, rather than at whatever later moment the JIT first needs
    /// the missing type.
    /// </summary>
    /// <exception cref="PluginDependencyNotTrustedException">A dependency's hash is not trusted.</exception>
    public Assembly LoadPlugin(byte[] verifiedBytes)
    {
        var root = LoadFromStream(new MemoryStream(verifiedBytes, writable: false));
        if (root.GetName().Name is { } rootName)
        {
            _loaded[rootName] = root;
        }

        var pending = new Queue<Assembly>();
        pending.Enqueue(root);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            foreach (var reference in pending.Dequeue().GetReferencedAssemblies())
            {
                if (reference.Name is null || !visited.Add(reference.Name))
                {
                    continue;
                }
                if (LoadFromPluginDirectory(reference) is { } dependency)
                {
                    pending.Enqueue(dependency);
                }
            }
        }
        return root;
    }

    protected override Assembly? Load(AssemblyName assemblyName) => LoadFromPluginDirectory(assemblyName);

    /// <summary>
    /// Returns the assembly if it is (trusted and) in the plugin directory; null to defer to
    /// the host for anything the host provides or the directory does not contain.
    /// </summary>
    private Assembly? LoadFromPluginDirectory(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrEmpty(name) || name.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }
        if (_loaded.TryGetValue(name, out var already))
        {
            return already;
        }
        if (IsProvidedByHost(assemblyName))
        {
            return null;
        }

        var path = Path.Combine(_directory, name + ".dll");
        if (!File.Exists(path))
        {
            return null; // the default context reports it missing in the usual way
        }

        var bytes = File.ReadAllBytes(path);
        RequireTrusted(bytes, Path.GetFileName(path));
        var assembly = LoadFromStream(new MemoryStream(bytes, writable: false));
        _loaded[name] = assembly;
        return assembly;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // Only a bare library name is looked for in the plugin directory; a path is the
        // plugin's own business and goes through the normal native loader.
        if (string.IsNullOrEmpty(unmanagedDllName) || Path.GetFileName(unmanagedDllName) != unmanagedDllName)
        {
            return IntPtr.Zero;
        }

        string[] candidates =
        [
            unmanagedDllName,
            unmanagedDllName + ".dll",
            unmanagedDllName + ".so",
            "lib" + unmanagedDllName + ".so",
            unmanagedDllName + ".dylib",
            "lib" + unmanagedDllName + ".dylib",
        ];
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(_directory, candidate);
            if (!File.Exists(path))
            {
                continue;
            }
            // A native library can only be loaded by path, so unlike the managed case the
            // check and the load are two reads of the file. The hash gate still means a
            // library nobody approved is never loaded by name.
            RequireTrusted(File.ReadAllBytes(path), candidate);
            return LoadUnmanagedDllFromPath(path);
        }
        return IntPtr.Zero;
    }

    private void RequireTrusted(byte[] bytes, string fileName)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!_trustedHashes.Contains(hash))
        {
            throw new PluginDependencyNotTrustedException(_pluginFile, fileName, hash);
        }
    }

    /// <summary>
    /// True when the default context already has, or can itself supply, this assembly.
    /// </summary>
    private static bool IsProvidedByHost(AssemblyName assemblyName)
    {
        foreach (var loaded in Default.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        try
        {
            // By simple name only: a plugin built against a slightly different version of a
            // framework assembly still gets the host's copy.
            Default.LoadFromAssemblyName(new AssemblyName(assemblyName.Name!));
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// A plugin whose own hash is trusted needs an assembly from its folder whose hash is not.
/// Approve <see cref="Sha256"/> in the <see cref="PluginTrustStore"/> to allow it.
/// </summary>
public sealed class PluginDependencyNotTrustedException(string pluginFile, string dependencyFile, string sha256)
    : FileLoadException(
        $"Plugin '{pluginFile}' needs '{dependencyFile}' (sha256 {sha256}), which is not in the trusted list. " +
        "No code from this plugin was run.", dependencyFile)
{
    public string PluginFile { get; } = pluginFile;
    public string DependencyFile { get; } = dependencyFile;
    public string Sha256 { get; } = sha256;
}
