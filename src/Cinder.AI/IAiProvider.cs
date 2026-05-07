namespace Cinder.AI;

/// <summary>
/// Cinder's BYOM (bring-your-own-model) AI provider contract. Concrete adapters (Ollama,
/// LM Studio, OpenAI-compatible) land in Phase 9. The interface is reserved here so other
/// modules can take an <see cref="IAiProvider"/> dependency without forward references.
/// </summary>
public interface IAiProvider
{
    string Id { get; }
    string DisplayName { get; }
    AiProviderCapabilities Capabilities { get; }

    Task<bool> HealthCheckAsync(CancellationToken ct);
    Task<string> CompleteAsync(AiPrompt prompt, CancellationToken ct);
    IAsyncEnumerable<string> StreamCompletionAsync(AiPrompt prompt, CancellationToken ct);
}

public sealed record AiProviderCapabilities(
    bool SupportsStreaming,
    bool SupportsToolCalling,
    bool SupportsVision,
    int MaxContextTokens,
    bool LocalOnly);

public sealed record AiPrompt(
    string SystemMessage,
    IReadOnlyList<AiMessage> Messages,
    AiPromptOptions Options);

public sealed record AiMessage(string Role, string Content);

public sealed record AiPromptOptions(int MaxTokens = 1024, double Temperature = 0.2);
