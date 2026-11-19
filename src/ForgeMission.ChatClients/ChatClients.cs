using System.ClientModel;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Manifest;
using ForgeMission.Core.Runtime;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace ForgeMission.ChatClients;

public static class ChatClients
{
    public static IExpertRunner Build(ProviderProfile profile) =>
        new DirectExpertRunner(BuildChatClient(profile));

    public static IChatClient BuildChatClient(ProviderProfile profile)
    {
        ProviderKeyGuard.EnsureKeyMatchesProvider(profile);
        return profile.Provider.ToLowerInvariant() switch
        {
            "openai"            => BuildOpenAiClient(profile),
            "azure"             => BuildAzureClient(profile),
            "ollama"            => BuildOllamaClient(profile),
            "anthropic"         => BuildAnthropicClient(profile),
            "xai"               => BuildXaiClient(profile),
            _ => throw new InvalidOperationException($"Unknown provider '{profile.Provider}'")
        };
    }

    private static IChatClient BuildOpenAiClient(ProviderProfile profile)
    {
        var options = EndpointOptions(profile.Endpoint);
#pragma warning disable OPENAI001 // Existing SDK Responses adapter is the selected OpenAI protocol.
        return new OpenAIClient(new ApiKeyCredential(profile.ApiKey ?? string.Empty), options)
            .GetResponsesClient().AsIChatClient(profile.Model)
            .AsBuilder().ConfigureOptions(chatOptions => chatOptions.RawRepresentationFactory = _ =>
                new CreateResponseOptions { StoredOutputEnabled = false, IncludedProperties = { "reasoning.encrypted_content" } })
            .Use(OpenAiResponseFailures.GetResponseAsync, OpenAiResponseFailures.GetStreamingResponseAsync)
            .Build();
#pragma warning restore OPENAI001
    }

    private static IChatClient BuildAzureClient(ProviderProfile profile) =>
        ChatCompletionsClient(profile.ApiKey ?? string.Empty, EndpointOptions(profile.Endpoint), profile.Model);

    // Ollama is OpenAI-compatible — point the OpenAI client at the local Ollama endpoint.
    private static IChatClient BuildOllamaClient(ProviderProfile profile) =>
        ChatCompletionsClient("ollama", EndpointOptions(EndpointOrDefault(profile, "http://localhost:11434/v1")), profile.Model);

    // xAI (Grok) is OpenAI-compatible — point the OpenAI client at api.x.ai.
    private static IChatClient BuildXaiClient(ProviderProfile profile) =>
        ChatCompletionsClient(profile.ApiKey ?? string.Empty, EndpointOptions(EndpointOrDefault(profile, "https://api.x.ai/v1")), profile.Model);

    private static IChatClient BuildAnthropicClient(ProviderProfile profile) => new AnthropicChatClient(profile);

    // OpenAI chat-completions protocol, shared by the OpenAI-compatible providers.
    private static IChatClient ChatCompletionsClient(string apiKey, OpenAIClientOptions options, string model) =>
        new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(model)
            .AsIChatClient();

    // An unset endpoint leaves the SDK default in place.
    private static OpenAIClientOptions EndpointOptions(string? endpoint)
    {
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(endpoint))
            options.Endpoint = new Uri(endpoint);
        return options;
    }

    private static string EndpointOrDefault(ProviderProfile profile, string defaultEndpoint) =>
        string.IsNullOrWhiteSpace(profile.Endpoint) ? defaultEndpoint : profile.Endpoint;
}
