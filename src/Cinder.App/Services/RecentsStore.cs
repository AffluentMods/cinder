using System.Text.Json;

namespace Cinder.App.Services;

/// <summary>
/// Persisted "recent cases" / "recent evidence" lists shown on the Home dashboard. Backed by a
/// single JSON file at <c>%LOCALAPPDATA%\Cinder\recents.json</c>. Load on startup, save after
/// every change — the lists are tiny so we don't bother with a debounce.
/// </summary>
public sealed class RecentsStore
{
    private const int MaxItems = 12;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;

    public RecentsStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cinder", "recents.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
    }

    public RecentsSnapshot Load()
    {
        if (!File.Exists(_path))
        {
            return new RecentsSnapshot();
        }
        try
        {
            return JsonSerializer.Deserialize<RecentsSnapshot>(File.ReadAllText(_path))
                ?? new RecentsSnapshot();
        }
        catch
        {
            // Corrupt or version-skewed file. Discard rather than crashing the dashboard.
            return new RecentsSnapshot();
        }
    }

    public void Save(RecentsSnapshot snapshot)
    {
        // Truncate before writing — the in-memory representation is what gets persisted.
        var trimmed = snapshot with
        {
            Cases = snapshot.Cases.Take(MaxItems).ToList(),
            Evidence = snapshot.Evidence.Take(MaxItems).ToList(),
        };
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(trimmed, Json));
        }
        catch
        {
            // Recents are best-effort; never crash on disk-write failures.
        }
    }

    public string Path => _path;
}

public sealed record RecentsSnapshot
{
    public List<RecentCaseEntry> Cases { get; init; } = new();
    public List<RecentEvidenceEntry> Evidence { get; init; } = new();
}

public sealed record RecentCaseEntry(
    Guid Id,
    string Name,
    string Examiner,
    string Path,
    DateTimeOffset OpenedUtc);

public sealed record RecentEvidenceEntry(
    string Path,
    DateTimeOffset OpenedUtc);
