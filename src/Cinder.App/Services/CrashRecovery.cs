using System.Text.Json;

namespace Cinder.App.Services;

/// <summary>
/// Per-action snapshot of mutable case state. Cinder writes a tiny JSON file every time the
/// user changes something (bookmark added, tag set, note edited) so that if the process dies,
/// we can restore unsaved state on relaunch. The custody log itself is already durable —
/// this is for things like "open tabs" / "selected offset" / "current case path".
/// </summary>
public sealed class CrashRecovery
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public CrashRecovery(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cinder", "recovery", "session.json");

    public void Save<T>(T snapshot)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    public T? Load<T>() where T : class
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(_path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Clear()
    {
        try { File.Delete(_path); } catch { }
    }
}
