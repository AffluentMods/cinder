using System.Collections.ObjectModel;
using Cinder.AI;
using Cinder.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

/// <summary>BYOM AI copilot UI. Plug an Ollama / LM Studio / OpenAI-compatible endpoint via
/// Settings, then chat against it here.</summary>
public sealed partial class AiCopilotToolViewModel : ViewModelBase
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly SettingsStore _settings = new();

    [ObservableProperty]
    private string _providerId = "ollama";

    [ObservableProperty]
    private string _endpoint = "http://localhost:11434";

    [ObservableProperty]
    private string _model = "llama3";

    [ObservableProperty]
    private string? _apiKey;

    public AiCopilotToolViewModel()
    {
        // Hydrate provider config from settings.json — picks up the API key the user typed in
        // Settings dialog (decrypted automatically by SettingsStore on load).
        try
        {
            var s = _settings.Load();
            if (s.AiProvider.TryGetValue("id", out var id) && !string.IsNullOrEmpty(id)) ProviderId = id;
            if (s.AiProvider.TryGetValue("endpoint", out var ep) && !string.IsNullOrEmpty(ep)) Endpoint = ep;
            if (s.AiProvider.TryGetValue("model", out var m) && !string.IsNullOrEmpty(m)) Model = m;
            if (s.AiProvider.TryGetValue("apiKey", out var key) && !string.IsNullOrEmpty(key)) ApiKey = key;
        }
        catch { /* first launch — defaults are fine */ }
    }

    [ObservableProperty]
    private string _systemPrompt = "You are a forensic-analyst assistant. Answer based ONLY on facts the user provides. Cite paths and timestamps.";

    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string? _statusLine;

    public ObservableCollection<ChatMessageVm> Messages { get; } = new();

    public IReadOnlyList<string> ProviderOptions { get; } =
        ["ollama", "lm-studio", "openai-compatible", "openai", "disabled"];

    [RelayCommand]
    private async Task SendAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Input) || IsStreaming)
        {
            return;
        }
        var prompt = new AiPrompt(
            SystemMessage: SystemPrompt,
            Messages: Messages.Select(m => new AiMessage(m.Role, m.Content)).Append(new AiMessage("user", Input)).ToList(),
            Options: new AiPromptOptions(MaxTokens: 1500, Temperature: 0.2));
        Messages.Add(new ChatMessageVm("user", Input));
        var assistant = new ChatMessageVm("assistant", "");
        Messages.Add(assistant);
        Input = "";

        IAiProvider provider = BuildProvider();

        IsStreaming = true;
        StatusLine = "Streaming…";
        try
        {
            var sb = new System.Text.StringBuilder();
            await foreach (var chunk in provider.StreamCompletionAsync(prompt, ct))
            {
                sb.Append(chunk);
                assistant.Content = sb.ToString();
            }
            StatusLine = $"Done · {sb.Length} chars.";
        }
        catch (Exception ex)
        {
            assistant.Content = $"[error] {ex.Message}";
            StatusLine = "Failed.";
        }
        finally
        {
            IsStreaming = false;
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Messages.Clear();
        StatusLine = null;
    }

    /// <summary>
    /// Health-check the configured provider so the user knows it works BEFORE sending a real
    /// prompt. For Ollama this hits /api/tags; for OpenAI-compatible it GETs /models. A pass
    /// here doesn't guarantee the chosen Model is available, but it confirms the endpoint
    /// answers and auth (if any) works.
    /// </summary>
    [RelayCommand]
    private async Task TestAsync(CancellationToken ct)
    {
        StatusLine = "Testing connection…";
        try
        {
            IAiProvider provider = BuildProvider();
            var ok = await provider.HealthCheckAsync(ct);
            StatusLine = ok
                ? $"✓ {ProviderId} reachable at {Endpoint}."
                : $"✗ {ProviderId} did not respond at {Endpoint}.";
        }
        catch (Exception ex)
        {
            StatusLine = $"✗ {ex.Message}";
        }
    }

    private IAiProvider BuildProvider() => ProviderId switch
    {
        "ollama" => new OllamaProvider(_http, Model, new Uri(Endpoint)),
        "lm-studio" => new LmStudioProvider(_http, Model, new Uri(Endpoint)),
        "openai-compatible" or "openai" =>
            new OpenAiCompatibleProvider(_http, ProviderId, ProviderId, new Uri(Endpoint), Model, () => ApiKey),
        _ => new DisabledProvider(),
    };
}

public sealed partial class ChatMessageVm : ViewModelBase
{
    [ObservableProperty]
    private string _content;
    public string Role { get; }

    public ChatMessageVm(string role, string content)
    {
        Role = role;
        _content = content;
    }
}
