using System.ClientModel;
using Anthropic;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Manifest;
using ForgeMission.Core.Runtime;
using Microsoft.Extensions.AI;
using OpenAI;

namespace ForgeMission.ChatClients;

public static class ChatClients
{
    public static IExpertRunner Build(ProviderProfile profile) =>
        new DirectExpertRunner(BuildChatClient(profile));

    public static IChatClient BuildChatClient(ProviderProfile profile) =>
        profile.Provider.ToLowerInvariant() switch
        {
            "openai" or "azure" => BuildOpenAiClient(profile),
            "ollama"            => BuildOllamaClient(profile),
            "anthropic"         => BuildAnthropicClient(profile),
            "xai"               => BuildXaiClient(profile),
            _ => throw new InvalidOperationException($"Unknown provider '{profile.Provider}'")
        };

    private static IChatClient BuildOpenAiClient(ProviderProfile profile)
    {
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(profile.Endpoint))
            options.Endpoint = new Uri(profile.Endpoint);
        return new OpenAIClient(new ApiKeyCredential(profile.ApiKey ?? string.Empty), options)
            .GetChatClient(profile.Model)
            .AsIChatClient();
    }

    // Ollama is OpenAI-compatible — point the OpenAI client at the local Ollama endpoint.
    private static IChatClient BuildOllamaClient(ProviderProfile profile)
    {
        var endpoint = string.IsNullOrWhiteSpace(profile.Endpoint)
            ? "http://localhost:11434/v1"
            : profile.Endpoint;
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
        return new OpenAIClient(new ApiKeyCredential("ollama"), options)
            .GetChatClient(profile.Model)
            .AsIChatClient();
    }

    // xAI (Grok) is OpenAI-compatible — point the OpenAI client at api.x.ai.
    private static IChatClient BuildXaiClient(ProviderProfile profile)
    {
        var endpoint = string.IsNullOrWhiteSpace(profile.Endpoint)
            ? "https://api.x.ai/v1"
            : profile.Endpoint;
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
        return new OpenAIClient(new ApiKeyCredential(profile.ApiKey ?? string.Empty), options)
            .GetChatClient(profile.Model)
            .AsIChatClient();
    }

    private static IChatClient BuildAnthropicClient(ProviderProfile profile)
    {
        var client = string.IsNullOrWhiteSpace(profile.Endpoint)
            ? new AnthropicClient(profile.ApiKey ?? string.Empty)
            : new AnthropicClient(
                httpClient: null,
                baseUri: new Uri(profile.Endpoint),
                authorizations:
                [
                    new EndPointAuthorization
                    {
                        Type = "ApiKey",
                        Location = "Header",
                        Name = "x-api-key",
                        Value = profile.ApiKey ?? string.Empty
                    }
                ]);

        return new AnthropicResponseFormatChatClient(client, profile.Model);
    }
}

// tryAGI.Anthropic implements IChatClient directly, but receives native structured-output
// configuration only through RawRepresentationFactory rather than ChatOptions.ResponseFormat,
// and its streaming adapter keeps only text deltas (no usage, no stop reason).
internal sealed class AnthropicResponseFormatChatClient(AnthropicClient client, string modelId) : IChatClient
{
    private const int DefaultMaxTokens = 4096;
    private readonly IChatClient inner = client;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options = EnsureModelId(options);
        TranslateNativeOptions(options);
        return inner.GetResponseAsync(messages, options, cancellationToken);
    }

    // Streaming never uses ResponseFormat (a judge's envelope is a prompt-level instruction). A plain
    // text turn streams from the native event stream so its usage reaches billing (Phase 53.8);
    // tool-mode streaming still goes through the SDK adapter.
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options = EnsureModelId(options);
        TranslateNativeOptions(options);
        return AnthropicTextStream.Accepts(messages, options)
            ? AnthropicTextStream.StreamAsync(client, messages, options, cancellationToken)
            : inner.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();

    // The SDK otherwise sends a 250-token limit on calls without structured output (streamed and
    // tool-mode calls), cutting a reply short; every call gets the same default (Phase 53.8).
    private ChatOptions EnsureModelId(ChatOptions? options)
    {
        options ??= new ChatOptions();
        options.ModelId ??= modelId;
        options.MaxOutputTokens ??= DefaultMaxTokens;
        return options;
    }

    // tryAGI.Anthropic ignores ChatOptions.ResponseFormat and AllowMultipleToolCalls; both reach the
    // wire only through one native request.
    private static void TranslateNativeOptions(ChatOptions options)
    {
        var format = options.ResponseFormat as ChatResponseFormatJson;
        var oneToolCall = options.AllowMultipleToolCalls == false && options.Tools is { Count: > 0 };
        if (format is null && !oneToolCall)
            return;

        options.RawRepresentationFactory = _ =>
        {
            var request = new CreateMessageParams
            {
                Model = string.Empty,
                Messages = [],
                MaxTokens = options.MaxOutputTokens ?? DefaultMaxTokens,
            };
            if (format is not null)
                request.OutputConfig = new OutputConfig
                {
                    Format = new JsonOutputFormat(format.Schema ?? throw new InvalidOperationException(
                        "Anthropic structured output requires a JSON schema."))
                };
            if (oneToolCall)
                request.ToolChoice = new ToolChoiceAuto { DisableParallelToolUse = true };
            return request;
        };
    }
}
