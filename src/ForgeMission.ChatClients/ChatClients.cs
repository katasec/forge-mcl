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
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(profile.Endpoint))
            options.Endpoint = new Uri(profile.Endpoint);
#pragma warning disable OPENAI001 // Existing SDK Responses adapter is the selected OpenAI protocol.
        return new OpenAIClient(new ApiKeyCredential(profile.ApiKey ?? string.Empty), options)
            .GetResponsesClient().AsIChatClient(profile.Model)
            .AsBuilder().ConfigureOptions(chatOptions => chatOptions.RawRepresentationFactory = _ =>
                new CreateResponseOptions { StoredOutputEnabled = false, IncludedProperties = { "reasoning.encrypted_content" } })
            .Use(OpenAiResponseFailures.GetResponseAsync, OpenAiResponseFailures.GetStreamingResponseAsync)
            .Build();
#pragma warning restore OPENAI001
    }

    private static IChatClient BuildAzureClient(ProviderProfile profile)
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

    private static IChatClient BuildAnthropicClient(ProviderProfile profile) => new AnthropicChatClient(profile);
}
