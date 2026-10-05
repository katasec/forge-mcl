using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.AI;

namespace ForgeMission.ChatClients;

/// <summary>
/// Turns an Anthropic SDK <see cref="ApiException"/> into an error a user can act on. The SDK puts
/// the provider's explanation in the response body, not in <see cref="Exception.Message"/>; a
/// streamed call failed as just "Bad Request" (Phase 69).
/// </summary>
internal static class AnthropicProviderError
{
    private const string Summary = "The model provider (Anthropic) returned an error. Check your provider account.";

    public static InvalidOperationException From(ApiException error) =>
        Detail(error) is { } detail
            ? new InvalidOperationException($"{Summary} Details: {detail}", error)
            : new InvalidOperationException(Summary, error);

    /// <summary>Streaming errors surface while enumerating, so the catch wraps each move.</summary>
    public static async IAsyncEnumerable<ChatResponseUpdate> Translate(
        IAsyncEnumerable<ChatResponseUpdate> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var updates = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            try { hasNext = await updates.MoveNextAsync(); }
            catch (ApiException error) { throw From(error); }

            if (!hasNext)
                yield break;
            yield return updates.Current;
        }
    }

    // A whole response keeps the raw body; a streamed one keeps only the parsed error object.
    private static string? Detail(ApiException error) =>
        MessageOf(error.ResponseBody ?? (error as ApiException<ErrorResponse>)?.ResponseObject?.ToJson());

    // Anthropic error body: {"type":"error","error":{"type":"...","message":"..."}}.
    private static string? MessageOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var body))
                return null;
            if (body.ValueKind != JsonValueKind.Object)
                return null;
            return body.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
