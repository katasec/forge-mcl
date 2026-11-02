using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Katasec.Anthropic.Models;
using Microsoft.Extensions.AI;

namespace ForgeMission.ChatClients;

/// <summary>
/// Turns an Anthropic Messages SSE stream into <see cref="ChatResponseUpdate"/>s: text deltas, one
/// update per completed tool call, the finish reason, and exactly one trailing
/// <see cref="UsageContent"/>. Thinking blocks and pings are ignored.
/// </summary>
internal sealed class AnthropicStream
{
    private readonly Dictionary<int, (string Id, string Name, StringBuilder Json)> _calls = [];
    private AnthropicUsage _input = new();
    private int _outputTokens;
    private string? _id;
    private string? _model;
    private bool _stopped;

    public static async IAsyncEnumerable<ChatResponseUpdate> ReadAsync(
        Stream body, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = new AnthropicStream();
        await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            if (stream.Handle(item.EventType, item.Data) is { } update)
                yield return update;
            if (stream._stopped)
                yield break;
        }
        throw new InvalidOperationException("The model provider (Anthropic) stream ended before the reply was complete.");
    }

    private ChatResponseUpdate? Handle(string eventName, string data)
    {
        try { return Dispatch(eventName, data); }
        catch (JsonException error) { throw AnthropicProviderError.Create(detail: null, error); }
    }

    private ChatResponseUpdate? Dispatch(string eventName, string data)
    {
        switch (eventName)
        {
            case "message_start":
                var start = Parse(data, AnthropicJsonContext.Default.AnthropicMessageStart).Message;
                (_input, _id, _model) = (start.Usage, start.Id, start.Model);
                return null;
            case "content_block_start":
                var block = Parse(data, AnthropicJsonContext.Default.AnthropicContentBlockStart);
                if (block.ContentBlock.Type == "tool_use")
                    _calls[block.Index] = (block.ContentBlock.Id ?? string.Empty, block.ContentBlock.Name ?? string.Empty, new StringBuilder());
                return null;
            case "content_block_delta":
                return Delta(data);
            case "content_block_stop":
                return CompleteCall(Parse(data, AnthropicJsonContext.Default.AnthropicContentBlockStop).Index);
            case "message_delta":
                var delta = Parse(data, AnthropicJsonContext.Default.AnthropicMessageDelta);
                _outputTokens = delta.Usage.OutputTokens;
                if (delta.Delta.StopReason is not { } stopReason)
                    return null;
                var finish = Update([]);
                finish.FinishReason = AnthropicMessageMapping.FinishReason(stopReason);
                return finish;
            case "message_stop":
                _stopped = true;
                return Update([new UsageContent(AnthropicMessageMapping.Usage(_input, _outputTokens))]);
            case "error":
                throw AnthropicProviderError.FromHttp(status: null, data);
            default:
                return null;
        }
    }

    // The delta type decides the shape; thinking and signature deltas are ignored.
    private ChatResponseUpdate? Delta(string data)
    {
        using var document = JsonDocument.Parse(data);
        if (!document.RootElement.TryGetProperty("delta", out var deltaElement)
            || deltaElement.ValueKind != JsonValueKind.Object
            || !deltaElement.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
            throw new JsonException("Anthropic sent a content_block_delta without a delta type.");
        var type = typeElement.GetString();
        if (type == "text_delta")
            return Update([new TextContent(document.RootElement.Deserialize(AnthropicJsonContext.Default.AnthropicContentBlockDelta)!.Delta.Text)]);
        if (type == "input_json_delta")
        {
            var toolDelta = document.RootElement.Deserialize(AnthropicJsonContext.Default.AnthropicContentBlockToolDelta)!;
            if (_calls.TryGetValue(toolDelta.Index, out var call))
                call.Json.Append(toolDelta.Delta.PartialJson);
        }
        return null;
    }

    private ChatResponseUpdate? CompleteCall(int index)
    {
        if (!_calls.Remove(index, out var call))
            return null;
        using var input = JsonDocument.Parse(call.Json.Length == 0 ? "{}" : call.Json.ToString());
        return Update([new FunctionCallContent(call.Id, call.Name, AnthropicMessageMapping.Arguments(input.RootElement))]);
    }

    private ChatResponseUpdate Update(IList<AIContent> contents) =>
        new(ChatRole.Assistant, contents) { ResponseId = _id, ModelId = _model };

    private static T Parse<T>(string data, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(data, typeInfo) ?? throw new JsonException($"Anthropic sent a null {typeof(T).Name} event.");
}
