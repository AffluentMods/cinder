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

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SendAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Input) || IsStreaming)
        {
            return;
        }
        // Checked before anything is added to the conversation: a mistyped address must leave
        // the question in the box and say what is wrong, not take the application down.
        if (!TryBuildProvider(out var provider, out var problem))
        {
            StatusLine = problem;
            return;
        }

        var prompt = new AiPrompt(
            SystemMessage: SystemPrompt,
            // Earlier failures are shown in the conversation but are not something the model said.
            Messages: Messages.Where(m => !m.IsError).Select(m => new AiMessage(m.Role, m.Content))
                .Append(new AiMessage("user", Input)).ToList(),
            Options: new AiPromptOptions(MaxTokens: 1500, Temperature: 0.2));
        Messages.Add(new ChatMessageVm("user", Input));
        var assistant = new ChatMessageVm("assistant", "");
        Messages.Add(assistant);
        Input = "";

        IsStreaming = true;
        StatusLine = "Waiting for the model…";
        var sb = new System.Text.StringBuilder();
        try
        {
            await foreach (var chunk in provider.StreamCompletionAsync(prompt, ct))
            {
                sb.Append(chunk);
                assistant.Content = sb.ToString();
            }
            StatusLine = $"Reply complete, {sb.Length:N0} characters.";
        }
        catch (OperationCanceledException)
        {
            assistant.IsError = sb.Length == 0;
            if (sb.Length == 0)
            {
                assistant.Content = "(stopped before the model replied)";
            }
            StatusLine = "Stopped.";
        }
        catch (Exception ex)
        {
            assistant.IsError = true;
            assistant.Content = $"Could not get a reply: {ex.Message}";
            StatusLine = $"Could not get a reply from {ProviderId} at {Endpoint}.";
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
        if (!TryBuildProvider(out var provider, out var problem))
        {
            StatusLine = problem;
            return;
        }
        StatusLine = "Testing the connection…";
        try
        {
            var ok = await provider.HealthCheckAsync(ct);
            StatusLine = ok
                ? $"✓ {ProviderId} answered at {Endpoint}."
                : $"✗ {ProviderId} did not answer at {Endpoint}. Check that it is running and that the address is right.";
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Stopped.";
        }
        catch (Exception ex)
        {
            StatusLine = $"✗ Could not reach {Endpoint}: {ex.Message}";
        }
    }

    /// <summary>
    /// Builds the provider for the current settings, or explains why it cannot. Never throws:
    /// the endpoint is free text and people type <c>192.168.1.5:11434</c> without a scheme.
    /// </summary>
    private bool TryBuildProvider(out IAiProvider provider, out string? problem)
    {
        provider = new DisabledProvider();
        problem = null;
        if (ProviderId is not ("ollama" or "lm-studio" or "openai-compatible" or "openai"))
        {
            return true; // "disabled": the provider itself reports that it is switched off
        }
        if (!TryParseEndpoint(Endpoint, out var uri, out problem))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(Model))
        {
            problem = "Enter the name of the model to use, for example llama3.";
            return false;
        }
        provider = ProviderId switch
        {
            "ollama" => new OllamaProvider(_http, Model, uri),
            "lm-studio" => new LmStudioProvider(_http, Model, uri),
            _ => new OpenAiCompatibleProvider(_http, ProviderId, ProviderId, uri, Model, () => ApiKey),
        };
        return true;
    }

    /// <summary>
    /// Accepts a full http or https address. Anything else is refused with a sentence that
    /// says what to type; when the only thing missing is the scheme, the corrected address is
    /// offered.
    /// </summary>
    internal static bool TryParseEndpoint(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri, out string? problem)
    {
        uri = null;
        problem = null;
        var value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            problem = "Enter the server address, for example http://localhost:11434.";
            return false;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            && parsed.Host.Length > 0)
        {
            uri = parsed;
            return true;
        }
        var suggestion = !value.Contains("://", StringComparison.Ordinal)
                         && Uri.TryCreate("http://" + value, UriKind.Absolute, out var guess) && guess.Host.Length > 0
            ? $" Did you mean http://{value} ?"
            : "";
        problem = $"\"{value}\" is not a full address. It must start with http:// or https://, for example http://localhost:11434.{suggestion}";
        return false;
    }
}

public sealed partial class ChatMessageVm : ViewModelBase
{
    [ObservableProperty]
    private string _content;
    public string Role { get; }

    /// <summary>True for a line that reports a failure rather than something the model said. Not sent back to the model.</summary>
    public bool IsError { get; set; }

    public ChatMessageVm(string role, string content)
    {
        Role = role;
        _content = content;
    }
}
