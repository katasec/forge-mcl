using System.Net;
using System.Text.Json;
using Katasec.Anthropic.Models;

namespace ForgeMission.ChatClients;

/// <summary>
/// Turns an Anthropic failure into an error a user can act on, carrying the provider's
/// <c>error.message</c> when the body has one (Phase 69).
/// </summary>
internal static class AnthropicProviderError
{
    private const string Summary = "The model provider (Anthropic) returned an error. Check your provider account.";

    /// <summary>A non-2xx response (<paramref name="status"/> set) or an SSE <c>error</c> event (null).</summary>
    public static InvalidOperationException FromHttp(HttpStatusCode? status, string? body)
    {
        var reason = status is { } code ? $"Anthropic returned HTTP {(int)code}." : "Anthropic sent a stream error event.";
        return Create(MessageOf(body), new HttpRequestException(reason, null, status));
    }

    public static InvalidOperationException Create(string? detail, Exception inner) =>
        string.IsNullOrEmpty(detail)
            ? new InvalidOperationException(Summary, inner)
            : new InvalidOperationException($"{Summary} Details: {detail}", inner);

    // Anthropic error body: {"type":"error","error":{"type":"...","message":"..."}}.
    private static string? MessageOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            return JsonSerializer.Deserialize(body, AnthropicJsonContext.Default.AnthropicErrorResponse)?.Error?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
