using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cinder.App.ViewModels.Tools;

/// <summary>One section in the left activity rail (e.g. "Examine", "Analyze").</summary>
public sealed class ToolSection
{
    public string Title { get; init; } = "";
    public ObservableCollection<ToolViewModel> Tools { get; init; } = new();
}

/// <summary>
/// Base class for every "tool" surfaced in the left rail. Each derived class typically wraps a
/// service or sidecar (registry parser, super-timeline, report builder, etc.). Tools that aren't
/// yet wired to real evidence still expose the right loader UI so the user knows what they do.
/// </summary>
public abstract partial class ToolViewModel : ViewModelBase
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Icon { get; }
    public virtual string Phase => "";
    public virtual string Subtitle => "";

    /// <summary>"hex" | "filesystem" | "registry" | … — drives the content selector in the shell.</summary>
    public abstract string Kind { get; }

    /// <summary>Hint shown in the generic placeholder for tools that need evidence loaded.</summary>
    public virtual string? EmptyStateHint => null;

    /// <summary>Python packages a sidecar-driven tool needs in the bundled venv.</summary>
    public virtual IReadOnlyList<string> RequiredPythonPackages => Array.Empty<string>();

    /// <summary>
    /// Long-form help for the "?" affordance in the tool header. Plain text with newline
    /// paragraphs and "## Section" headings; rendered by the help flyout into bold/regular runs.
    /// Tools that don't override this get a placeholder.
    /// </summary>
    public virtual string HelpMarkdown =>
        $"## What this is\n{Title} — {Subtitle}\n\n" +
        "## Status\nDetailed help for this tool is not yet written. " +
        "Check ROADMAP.md for what's planned, or visit the project's GitHub for usage notes.";

    /// <summary>Whether the "?" button should appear in the tool header.</summary>
    public bool HasHelp => !string.IsNullOrWhiteSpace(HelpMarkdown);

    private IReadOnlyList<HelpBlock>? _helpBlocks;

    /// <summary>
    /// <see cref="HelpMarkdown"/> parsed into a list of typed blocks the view can render with a
    /// data-template per Kind. Parsing happens once and is cached. Moving the parser out of
    /// code-behind into the view-model means the help body is purely binding-driven (no
    /// fragile FindControl / Application.Current.Resources lookups).
    /// </summary>
    public IReadOnlyList<HelpBlock> HelpBlocks => _helpBlocks ??= ParseHelp(HelpMarkdown);

    [ObservableProperty]
    private bool _isSelected;

    private static IReadOnlyList<HelpBlock> ParseHelp(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return Array.Empty<HelpBlock>();
        }

        var blocks = new List<HelpBlock>();
        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count == 0)
            {
                return;
            }
            blocks.Add(new HelpBlock(HelpBlockKind.Paragraph,
                string.Join(" ", paragraph.Select(l => l.TrimEnd()))));
            paragraph.Clear();
        }

        foreach (var raw in source.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new HelpBlock(HelpBlockKind.Heading, line[3..].Trim().ToUpperInvariant()));
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new HelpBlock(HelpBlockKind.Bullet, line[2..].Trim(), Marker: "•"));
                continue;
            }
            var dot = line.IndexOf(". ", StringComparison.Ordinal);
            if (dot is > 0 and < 4 && int.TryParse(line[..dot], out _))
            {
                Flush();
                blocks.Add(new HelpBlock(HelpBlockKind.Bullet, line[(dot + 2)..].Trim(), Marker: line[..dot] + "."));
                continue;
            }
            paragraph.Add(line);
        }
        Flush();

        return blocks;
    }
}

/// <summary>One renderable block in a tool's help body.</summary>
public sealed record HelpBlock(HelpBlockKind Kind, string Text, string Marker = "")
{
    public bool IsHeading   => Kind == HelpBlockKind.Heading;
    public bool IsParagraph => Kind == HelpBlockKind.Paragraph;
    public bool IsBullet    => Kind == HelpBlockKind.Bullet;
}

public enum HelpBlockKind { Heading, Paragraph, Bullet }

/// <summary>
/// Generic "load evidence and call the sidecar" tool. Most parser tabs (registry / EVTX / etc.)
/// inherit this. Subclasses fill in <see cref="LoadAsync"/> with the actual sidecar invocation;
/// the view layer binds to the source-generated <c>LoadCommand</c> and to <see cref="Rows"/>.
/// </summary>
public abstract partial class SidecarToolViewModel : ToolViewModel
{
    public ObservableCollection<object> Rows { get; } = new();

    [ObservableProperty]
    private string? _evidencePath;

    [ObservableProperty]
    private string? _statusLine;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Human-readable hint shown in the empty state.</summary>
    public override string? EmptyStateHint => "Load evidence to populate this tool.";

    /// <summary>
    /// Subclasses run their sidecar and populate <see cref="Rows"/> here. Failures should be
    /// caught and surfaced via <see cref="ErrorMessage"/> rather than thrown.
    /// </summary>
    protected abstract Task LoadAsync(string evidencePath, CancellationToken ct);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task LoadEvidenceAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        EvidencePath = path;
        ErrorMessage = null;
        IsLoading = true;
        Rows.Clear();
        try
        {
            await LoadAsync(path, ct).ConfigureAwait(false);
            StatusLine = $"{Rows.Count:N0} entries";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusLine = "Failed.";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
