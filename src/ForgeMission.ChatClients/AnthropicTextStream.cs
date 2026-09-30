using System.Runtime.CompilerServices;
using Anthropic;
using Microsoft.Extensions.AI;

namespace ForgeMission.ChatClients;

/// <summary>
/// Streams a plain text Anthropic turn from the native Messages event stream. The SDK's
/// <see cref="IChatClient"/> streaming adapter keeps only text deltas, so a streamed turn lost its
/// token usage and billed 0+0 (Phase 53.8). This keeps usage and the stop reason: input tokens from
/// <c>message_start</c>, cumulative output tokens from the last <c>message_delta</c>, reported as
/// exactly one trailing <see cref="UsageContent"/>.
/// </summary>
internal static class AnthropicTextStream
{
    /// <summary>A turn this path can send as faithfully as the SDK adapter would: text-only
    /// messages, no tools, no native request override, and no sampling settings (which the SDK
    /// marks obsolete on its request type).</summary>
    public static bool Accepts(IEnumerable<ChatMessage> messages, ChatOptions options) =>
        options.Tools is not { Count: > 0 }
        && options.RawRepresentationFactory is null
        && options.Temperature is null
        && options.TopP is null
        && options.TopK is null
        && messages.All(IsPlainText);

    public static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        AnthropicClient client,
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Usage? started = null;
        var outputTokens = 0;

        await foreach (var streamEvent in client.CreateMessageAsStreamAsync(BuildRequest(messages, options), cancellationToken: cancellationToken))
        {
            if (streamEvent.IsMessageStart)
            {
                started = streamEvent.MessageStart!.Message.Usage;
                continue;
            }

            if (streamEvent.IsMessageDelta)
            {
                var delta = streamEvent.MessageDelta!;
                outputTokens = delta.Usage.OutputTokens;
                if (delta.Delta.StopReason is { } stopReason)
                    yield return new ChatResponseUpdate { Role = ChatRole.Assistant, FinishReason = FinishReason(stopReason) };
                continue;
            }

            if (streamEvent.IsContentBlockDelta && streamEvent.ContentBlockDelta!.Delta.IsTextDelta)
                yield return new ChatResponseUpdate(ChatRole.Assistant, streamEvent.ContentBlockDelta.Delta.TextDelta!.Text);
        }

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new UsageContent(UsageDetails(started, outputTokens))],
        };
    }

    private static bool IsPlainText(ChatMessage message) =>
        (message.Role == ChatRole.System || message.Role == ChatRole.User || message.Role == ChatRole.Assistant)
        && message.Contents.All(content => content is TextContent);

    private static CreateMessageParams BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions options)
    {
        var request = new CreateMessageParams
        {
            Model = options.ModelId ?? string.Empty,
            MaxTokens = options.MaxOutputTokens ?? throw new InvalidOperationException("A streamed Anthropic call needs MaxOutputTokens."),
            Messages = messages
                .Where(message => message.Role != ChatRole.System)
                .Select(message => new InputMessage
                {
                    Role = message.Role == ChatRole.Assistant ? InputMessageRole.Assistant : InputMessageRole.User,
                    Content = message.Text,
                })
                .ToList(),
            StopSequences = options.StopSequences,
        };

        var system = string.Join("\n\n", messages.Where(message => message.Role == ChatRole.System).Select(message => message.Text));
        if (system.Length > 0)
            request.System = system;
        return request;
    }

    // Same shape as the SDK's non-streaming usage, so a streamed and a whole-response turn bill alike.
    private static UsageDetails UsageDetails(Usage? started, int outputTokens)
    {
        var inputTokens = started?.InputTokens ?? 0;
        var details = new UsageDetails
        {
            InputTokenCount = inputTokens,
            OutputTokenCount = outputTokens,
            TotalTokenCount = inputTokens + outputTokens,
        };
        if (started?.CacheCreationInputTokens is { } cacheCreation)
            (details.AdditionalCounts ??= [])["CacheCreationInputTokens"] = cacheCreation;
        if (started?.CacheReadInputTokens is { } cacheRead)
            (details.AdditionalCounts ??= [])["CacheReadInputTokens"] = cacheRead;
        return details;
    }

    private static ChatFinishReason FinishReason(StopReason stopReason) => stopReason switch
    {
        StopReason.EndTurn or StopReason.StopSequence => ChatFinishReason.Stop,
        StopReason.MaxTokens => ChatFinishReason.Length,
        StopReason.ToolUse => ChatFinishReason.ToolCalls,
        _ => new ChatFinishReason(stopReason.ToString()),
    };
}
