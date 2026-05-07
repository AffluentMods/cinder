namespace Cinder.AI;

/// <summary>The default provider — does nothing, returns nothing. Cinder ships with this
/// active so the app is fully usable without configuring any AI backend.</summary>
public sealed class DisabledProvider : IAiProvider
{
    public string Id => "disabled";
    public string DisplayName => "AI assist disabled";
    public AiProviderCapabilities Capabilities { get; } = new(false, false, false, 0, true);

    public Task<bool> HealthCheckAsync(CancellationToken ct) => Task.FromResult(true);
    public Task<string> CompleteAsync(AiPrompt prompt, CancellationToken ct) => Task.FromResult("");
    public async IAsyncEnumerable<string> StreamCompletionAsync(AiPrompt prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}
