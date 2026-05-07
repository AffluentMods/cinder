using System.Collections.ObjectModel;

namespace Cinder.App.Services;

/// <summary>An action invocable from the command palette.</summary>
public sealed record CommandDescriptor(
    string Id,
    string Title,
    string? Subtitle,
    string Category,
    Func<CancellationToken, Task> Invoke);

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

    public IEnumerable<CommandDescriptor> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _commands;
        }

        return _commands.Where(c =>
            c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            c.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (c.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
    }
}
