using System.Collections.ObjectModel;
using Cinder.AI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cinder.App.ViewModels;

/// <summary>BYOM AI copilot UI. Plug an Ollama / LM Studio / OpenAI-compatible endpoint via
/// Settings, then chat against it here.</summary>
public sealed partial class AiCopilotToolViewModel : ViewModelBase
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    [ObservableProperty]
    private string _providerId = "ollama";

    [ObservableProperty]
    private string _endpoint = "http://localhost:11434";

    [ObservableProperty]
    private string _model = "llama3";

    [ObservableProperty]
    private string? _apiKey;

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

        IAiProvider provider = ProviderId switch
        {
            "ollama" => new OllamaProvider(_http, Model, new Uri(Endpoint)),
            "lm-studio" => new LmStudioProvider(_http, Model, new Uri(Endpoint)),
            "openai-compatible" or "openai" =>
                new OpenAiCompatibleProvider(_http, ProviderId, ProviderId, new Uri(Endpoint), Model, () => ApiKey),
            _ => new DisabledProvider(),
        };

        IsStreaming = true;
        StatusLine = "Streaming…";
        try
        {
            var sb = new System.Text.StringBuilder();
            await foreach (var chunk in provider.StreamCompletionAsync(prompt, ct).ConfigureAwait(false))
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
