using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace ForgeMission.ChatClients;

// The SDK omits generic error content for empty failed responses and response.failed SSE events.
// Keep their native interpretation at this provider boundary; Core receives only ErrorContent.
#pragma warning disable OPENAI001 // Native Responses failure inspection at the existing SDK boundary.
internal static class OpenAiResponseFailures
{
    public static async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, IChatClient inner, CancellationToken ct)
    {
        var response = await inner.GetResponseAsync(messages, options, ct);
        if (response.RawRepresentation is ResponseResult native
            && (native.Error is not null || native.Status == ResponseStatus.Failed)
            && !response.Messages.SelectMany(message => message.Contents).OfType<ErrorContent>().Any())
            response.Messages.Add(new ChatMessage(ChatRole.Assistant, [ProjectError(native)]));
        return response;
    }

    public static async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, IChatClient inner,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, ct))
        {
            if (update.RawRepresentation is StreamingResponseFailedUpdate failed
                && !update.Contents.OfType<ErrorContent>().Any())
                update.Contents.Add(ProjectError(failed.Response));
            yield return update;
        }
    }

    private static ErrorContent ProjectError(ResponseResult? response)
    {
        var message = response?.Error?.Message;
        return new ErrorContent(string.IsNullOrEmpty(message)
            ? "The model provider returned a failed response." : message);
    }
}
#pragma warning restore OPENAI001
