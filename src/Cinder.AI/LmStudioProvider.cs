namespace Cinder.AI;

/// <summary>LM Studio exposes the OpenAI-compatible endpoint by default. This is a thin
/// pre-configured wrapper.</summary>
public sealed class LmStudioProvider : OpenAiCompatibleProvider
{
    public LmStudioProvider(HttpClient http, string model = "local-model", Uri? endpoint = null)
        : base(http, "lm-studio", $"LM Studio · {model}",
               endpoint ?? new Uri("http://localhost:1234/v1/"), model,
               apiKeyAccessor: () => null,
               capabilities: new AiProviderCapabilities(
                    SupportsStreaming: true, SupportsToolCalling: false, SupportsVision: false,
                    MaxContextTokens: 8_192, LocalOnly: true))
    {
    }
}
