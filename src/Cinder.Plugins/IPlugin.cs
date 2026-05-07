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
    public IReadOnlyList<IPlugin> LoadFromDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var found = new List<IPlugin>();
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll"))
        {
            try
            {
                var asm = Assembly.LoadFrom(dll);
                foreach (var type in asm.GetExportedTypes()
                    .Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract))
                {
                    if (Activator.CreateInstance(type) is IPlugin plugin)
                    {
                        found.Add(plugin);
                    }
                }
            }
            catch
            {
                // Skip malformed plugin DLLs; surface in UI but don't crash the host.
            }
        }
        return found;
    }
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
