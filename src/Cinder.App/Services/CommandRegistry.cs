using System.Collections.ObjectModel;

namespace Cinder.App.Services;

/// <summary>An action invocable from the command palette.</summary>
public sealed record CommandDescriptor(
    string Id,
    string Title,
    string? Subtitle,
    string Category,
    Func<CancellationToken, Task> Invoke,
    string? Shortcut = null)
{
    public bool HasShortcut => !string.IsNullOrEmpty(Shortcut);
}

/// <summary>
/// Registry of named actions, surfaced via Ctrl+K. Phase 0 ships the registry + a few stub
/// actions; subsequent phases register more (Open Case, Hash File, Toggle Theme, etc.).
/// </summary>
public sealed class CommandRegistry
{
    private readonly ObservableCollection<CommandDescriptor> _commands = [];

    public IReadOnlyList<CommandDescriptor> Commands => _commands;

    public void Register(CommandDescriptor command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_commands.Any(c => c.Id == command.Id))
        {
            throw new InvalidOperationException($"Command '{command.Id}' is already registered.");
        }
        _commands.Add(command);
    }

    /// <summary>Swaps a registered command for an updated copy (same Id), keeping its position.</summary>
    public void Replace(CommandDescriptor command)
    {
        for (int i = 0; i < _commands.Count; i++)
        {
            if (_commands[i].Id == command.Id)
            {
                _commands[i] = command;
                return;
            }
        }
        throw new InvalidOperationException($"Command '{command.Id}' is not registered.");
    }

    private readonly List<string> _recent = [];

    /// <summary>Remembers an invoked command so it leads the list next time the palette opens.</summary>
    public void NoteInvoked(CommandDescriptor command)
    {
        _recent.Remove(command.Id);
        _recent.Insert(0, command.Id);
        if (_recent.Count > 8)
        {
            _recent.RemoveAt(_recent.Count - 1);
        }
    }

    /// <summary>
    /// Ranked search. Empty query: recently used first, then registration order. Otherwise a
    /// title prefix beats a word start beats a substring beats an in-order subsequence ("sptl"
    /// finds "Super-timeline"); category and subtitle matches rank below any title match.
    /// </summary>
    public IEnumerable<CommandDescriptor> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            var recent = _recent.Select(id => _commands.FirstOrDefault(c => c.Id == id)).OfType<CommandDescriptor>();
            return recent.Concat(_commands.Where(c => !_recent.Contains(c.Id)));
        }

        var q = query.Trim();
        return _commands
            .Select((c, i) => (c, i, score: Score(c, q)))
            .Where(t => t.score > 0)
            .OrderByDescending(t => t.score)
            .ThenBy(t => { var r = _recent.IndexOf(t.c.Id); return r >= 0 ? r : int.MaxValue; })
            .ThenBy(t => t.i)
            .Select(t => t.c)
            .ToList();
    }

    internal static int Score(CommandDescriptor c, string q)
    {
        var title = c.Title;
        if (title.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 100;
        var words = title.Split([' ', '-', '/', '(', '$'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 80;
        if (title.Contains(q, StringComparison.OrdinalIgnoreCase)) return 60;
        if (IsSubsequence(title, q)) return 40;
        if (c.Category.Contains(q, StringComparison.OrdinalIgnoreCase)) return 25;
        if (c.Subtitle?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) return 15;
        return 0;
    }

    private static bool IsSubsequence(string text, string q)
    {
        var j = 0;
        foreach (var ch in text)
        {
            if (j < q.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(q[j]))
            {
                j++;
            }
        }
        return j == q.Length && q.Length >= 3;
    }
}
