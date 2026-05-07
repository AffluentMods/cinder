using System.Text.Json;
using Cinder.Core.Cases;

namespace Cinder.Cases;

/// <summary>
/// Multi-case workspace. Tracks recent cases, the active case, and a mapping from case GUID
/// → SQLite path on disk. Persisted as a per-user JSON in the OS-standard config directory:
///   Windows: %LOCALAPPDATA%\Cinder\workspace.json
///   Linux:   ~/.config/cinder/workspace.json
/// </summary>
public sealed class Workspace
{
    public List<WorkspaceCase> RecentCases { get; init; } = new();
    public Guid? ActiveCaseId { get; set; }
    public string SchemaVersion { get; init; } = "1.0";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cinder", "workspace.json");

    public static Workspace LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
        {
            try
            {
                return JsonSerializer.Deserialize<Workspace>(File.ReadAllText(path)) ?? new Workspace();
            }
            catch { }
        }
        return new Workspace();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void RecordOpen(Guid id, string caseFilePath, string displayName)
    {
        RecentCases.RemoveAll(c => c.Id == id);
        RecentCases.Insert(0, new WorkspaceCase(id, caseFilePath, displayName, DateTimeOffset.UtcNow));
        if (RecentCases.Count > 25)
        {
            RecentCases.RemoveRange(25, RecentCases.Count - 25);
        }
        ActiveCaseId = id;
    }
}

public sealed record WorkspaceCase(Guid Id, string FilePath, string DisplayName, DateTimeOffset LastOpenedUtc);
