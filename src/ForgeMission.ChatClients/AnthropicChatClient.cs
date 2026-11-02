using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ForgeMission.Core.Manifest;
using Katasec.Anthropic.Models;
using Microsoft.Extensions.AI;

namespace ForgeMission.ChatClients;

/// <summary>
/// Calls Anthropic's <c>POST /v1/messages</c>, plain and streamed, with source-generated wire types
/// (Phase 77). Translation lives in <see cref="AnthropicMessageMapping"/> and <see cref="AnthropicStream"/>.
/// </summary>
internal sealed class AnthropicChatClient(ProviderProfile profile) : IChatClient
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/";

    // One pooled client per process: forge-runner builds a chat client for every run. Redirects are
    // off because following one would resend x-api-key to another host.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AllowAutoRedirect = false,
    });

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = AnthropicMessageMapping.ToRequest(messages, options, profile.Model, stream: false);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        return AnthropicMessageMapping.ToChatResponse(await ReadResponseAsync(response.Content, cancellationToken));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = AnthropicMessageMapping.ToRequest(messages, options, profile.Model, stream: true);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var update in AnthropicStream.ReadAsync(body, cancellationToken))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private async Task<HttpResponseMessage> SendAsync(
        AnthropicRequest request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var endpoint = string.IsNullOrWhiteSpace(profile.Endpoint) ? DefaultEndpoint : profile.Endpoint;
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/messages")
        {
            Content = JsonContent.Create(request, AnthropicJsonContext.Default.AnthropicRequest),
        };
        message.Headers.Add("x-api-key", profile.ApiKey ?? string.Empty);
        message.Headers.Add("anthropic-version", "2023-06-01");

        var response = await Http.SendAsync(message, completion, cancellationToken);
        if (response.IsSuccessStatusCode)
            return response;
        using (response)
            throw AnthropicProviderError.FromHttp(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static async Task<AnthropicResponse> ReadResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            return await content.ReadFromJsonAsync(AnthropicJsonContext.Default.AnthropicResponse, cancellationToken)
                ?? throw new JsonException("The Anthropic response body was null.");
        }
        catch (JsonException error)
        {
            throw AnthropicProviderError.Create(detail: null, error);
        }
    }
}
