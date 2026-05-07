using System.Text.Json;
using Cinder.Artifacts;

namespace Cinder.AI;

/// <summary>
/// Builds AI prompts from parsed Cinder artifacts. The model never sees raw evidence bytes —
/// only structured records the parsers have already produced. This keeps prompts small,
/// model-agnostic, and means even a 7B local model handles structured questions well.
/// </summary>
public static class PromptBuilder
{
    private const string DefaultSystemMessage =
        "You are a forensic-analyst assistant embedded in Cinder. Answer based ONLY on the " +
        "provided artifacts. If the artifacts do not support an answer, say so explicitly. " +
        "Cite specific timestamps, paths, and IDs from the artifacts. Never speculate beyond what is given.";

    public static AiPrompt SummarizeUserActivity(string user, IReadOnlyCollection<IArtifact> artifacts, DateTimeOffset from, DateTimeOffset to)
    {
        var rows = artifacts.Select(a => new
        {
            ts = a.Timestamp?.ToString("O"),
            source = a.Source,
            user = a.User,
            summary = a.Summary,
        }).ToArray();
        return new AiPrompt(
            SystemMessage: DefaultSystemMessage,
            Messages: [
                new AiMessage("user", $"""
                    Question: Summarize the user "{user}"'s activity between {from:u} and {to:u}.
                    Highlight any anomalies (off-hours activity, USB attachments, sensitive paths).
                    Artifacts (JSON, sorted by timestamp):
                    {JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = false })}
                    """)
            ],
            Options: new AiPromptOptions(MaxTokens: 1500, Temperature: 0.2));
    }

    public static AiPrompt ExplainProcessTree(IReadOnlyCollection<IArtifact> processArtifacts)
    {
        var rows = processArtifacts.Select(a => new { source = a.Source, summary = a.Summary, extras = a.Extras }).ToArray();
        return new AiPrompt(
            SystemMessage: DefaultSystemMessage,
            Messages: [
                new AiMessage("user", $"""
                    Question: What is anomalous about this process tree? Look for signed-system-binary parents with unexpected children, hollowed processes, parent-PID mismatches, and unusual command-lines.
                    Process artifacts:
                    {JsonSerializer.Serialize(rows)}
                    """)
            ],
            Options: new AiPromptOptions(MaxTokens: 1500, Temperature: 0.1));
    }

    public static AiPrompt ExplainRegistryKey(string keyPath, IReadOnlyDictionary<string, string>? values)
    {
        return new AiPrompt(
            SystemMessage: DefaultSystemMessage,
            Messages: [
                new AiMessage("user", $"""
                    Question: Explain what this registry key is used for and what its values mean. Be concise.
                    Path: {keyPath}
                    Values: {(values is null ? "(none)" : JsonSerializer.Serialize(values))}
                    """)
            ],
            Options: new AiPromptOptions(MaxTokens: 600, Temperature: 0.0));
    }

    public static AiPrompt DraftCaseSummary(string caseName, IReadOnlyCollection<IArtifact> bookmarks)
    {
        return new AiPrompt(
            SystemMessage: DefaultSystemMessage,
            Messages: [
                new AiMessage("user", $"""
                    Question: Draft an executive summary (4–6 paragraphs) for case "{caseName}" using only the bookmarked findings below. Group by theme.
                    Findings (JSON):
                    {JsonSerializer.Serialize(bookmarks.Select(a => new { ts = a.Timestamp?.ToString("O"), source = a.Source, summary = a.Summary }).ToArray())}
                    """)
            ],
            Options: new AiPromptOptions(MaxTokens: 2000, Temperature: 0.2));
    }
}
