using System.Buffers;
using System.Text;
using System.Text.Json;
using Katasec.Anthropic.Models;
using Microsoft.Extensions.AI;

namespace ForgeMission.ChatClients;

/// <summary>Translates between Microsoft.Extensions.AI chat types and the Anthropic wire types.</summary>
internal static class AnthropicMessageMapping
{
    private const int DefaultMaxTokens = 4096;

    public static AnthropicRequest ToRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, string model, bool stream)
    {
        if (options?.RawRepresentationFactory is not null)
            throw new NotSupportedException("The Anthropic client does not support ChatOptions.RawRepresentationFactory.");

        var tools = Tools(options);
        var system = string.Join("\n\n", messages.Where(message => message.Role == ChatRole.System).Select(message => message.Text));
        return new AnthropicRequest
        {
            Model = options?.ModelId ?? model,
            Messages = Messages(messages),
            System = system.Length > 0 ? Json(writer => writer.WriteStringValue(system)) : null,
            MaxTokens = options?.MaxOutputTokens ?? DefaultMaxTokens,
            Stream = stream,
            Tools = tools,
            ToolChoice = ToolChoice(options, tools),
            StopSequences = options?.StopSequences?.ToList(),
            Temperature = options?.Temperature,
            TopP = options?.TopP,
            TopK = options?.TopK,
            OutputConfig = OutputConfig(options?.ResponseFormat),
        };
    }

    public static ChatResponse ToChatResponse(AnthropicResponse response)
    {
        var contents = new List<AIContent>();
        foreach (var block in response.Content)
        {
            if (block.Type == "text")
                contents.Add(new TextContent(block.Text));
            else if (block.Type == "tool_use")
                contents.Add(new FunctionCallContent(block.Id ?? string.Empty, block.Name ?? string.Empty, Arguments(block.Input)));
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ResponseId = response.Id,
            ModelId = response.Model,
            FinishReason = FinishReason(response.StopReason),
            Usage = Usage(response.Usage, response.Usage.OutputTokens),
            RawRepresentation = response,
        };
    }

    /// <summary>A tool_use <c>input</c> object as call arguments, each property a cloned <see cref="JsonElement"/>.</summary>
    public static Dictionary<string, object?> Arguments(JsonElement? input)
    {
        var arguments = new Dictionary<string, object?>();
        if (input is { ValueKind: JsonValueKind.Object } value)
            foreach (var property in value.EnumerateObject())
                arguments[property.Name] = property.Value.Clone();
        return arguments;
    }

    public static ChatFinishReason FinishReason(string stopReason) => stopReason switch
    {
        "end_turn" or "stop_sequence" => ChatFinishReason.Stop,
        "max_tokens" => ChatFinishReason.Length,
        "tool_use" => ChatFinishReason.ToolCalls,
        _ => new ChatFinishReason(stopReason),
    };

    // Cache counts keep the keys billing already reads.
    public static UsageDetails Usage(AnthropicUsage input, int outputTokens)
    {
        var details = new UsageDetails
        {
            InputTokenCount = input.InputTokens,
            OutputTokenCount = outputTokens,
            TotalTokenCount = input.InputTokens + outputTokens,
        };
        if (input.CacheCreationInputTokens is { } cacheCreation)
            (details.AdditionalCounts ??= [])["CacheCreationInputTokens"] = cacheCreation;
        if (input.CacheReadInputTokens is { } cacheRead)
            (details.AdditionalCounts ??= [])["CacheReadInputTokens"] = cacheRead;
        return details;
    }

    // Consecutive tool messages become one user message holding every tool_result block.
    private static List<AnthropicMessage> Messages(IEnumerable<ChatMessage> messages)
    {
        var turns = new List<(string Role, List<AnthropicContentBlock> Blocks)>();
        var previousWasTool = false;
        foreach (var message in messages.Where(message => message.Role != ChatRole.System))
        {
            var isTool = message.Role == ChatRole.Tool;
            if (!(isTool && previousWasTool))
                turns.Add((message.Role == ChatRole.Assistant ? "assistant" : "user", []));
            turns[^1].Blocks.AddRange(message.Contents.Select(Block));
            previousWasTool = isTool;
        }

        return turns.Select(turn => new AnthropicMessage
        {
            Role = turn.Role,
            Content = JsonSerializer.SerializeToElement(turn.Blocks, AnthropicJsonContext.Default.ListAnthropicContentBlock),
        }).ToList();
    }

    private static AnthropicContentBlock Block(AIContent content) => content switch
    {
        TextContent text => new AnthropicContentBlock { Type = "text", Text = text.Text },
        FunctionCallContent call => new AnthropicContentBlock
        {
            Type = "tool_use", Text = null, Id = call.CallId, Name = call.Name, Input = Json(writer => WriteArguments(writer, call.Arguments)),
        },
        FunctionResultContent result => new AnthropicContentBlock
        {
            Type = "tool_result", Text = null, ToolUseId = result.CallId,
            Content = Json(writer => writer.WriteStringValue(ResultText(result.Result))),
            IsError = result.Exception is not null,
        },
        _ => throw new NotSupportedException($"The Anthropic client does not support {content.GetType().Name} content."),
    };

    private static List<AnthropicToolDefinition>? Tools(ChatOptions? options)
    {
        var tools = options?.Tools?.OfType<AIFunctionDeclaration>()
            .Select(tool => new AnthropicToolDefinition { Name = tool.Name, Description = tool.Description, InputSchema = tool.JsonSchema })
            .ToList();
        return tools is { Count: > 0 } ? tools : null;
    }

    private static AnthropicToolChoice? ToolChoice(ChatOptions? options, List<AnthropicToolDefinition>? tools)
    {
        var choice = options?.ToolMode switch
        {
            RequiredChatToolMode { RequiredFunctionName: { } name } => new AnthropicToolChoice { Type = "tool", Name = name },
            RequiredChatToolMode => new AnthropicToolChoice { Type = "any" },
            NoneChatToolMode => new AnthropicToolChoice { Type = "none" },
            _ => null,
        };
        if (options?.AllowMultipleToolCalls == false && tools is not null)
        {
            choice ??= new AnthropicToolChoice { Type = "auto" };
            choice.DisableParallelToolUse = true;
        }
        return choice;
    }

    private static JsonElement? OutputConfig(ChatResponseFormat? format)
    {
        if (format is not ChatResponseFormatJson json)
            return null;
        var schema = json.Schema ?? throw new InvalidOperationException("Anthropic structured output requires a JSON schema.");
        return Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("format");
            writer.WriteString("type", "json_schema");
            writer.WritePropertyName("schema");
            schema.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static void WriteArguments(Utf8JsonWriter writer, IDictionary<string, object?>? arguments)
    {
        writer.WriteStartObject();
        foreach (var (name, value) in arguments ?? new Dictionary<string, object?>())
        {
            writer.WritePropertyName(name);
            WriteValue(writer, value);
        }
        writer.WriteEndObject();
    }

    private static string ResultText(object? result) => result switch
    {
        string text => text,
        JsonElement element => element.GetRawText(),
        _ => Encoding.UTF8.GetString(Write(writer => WriteValue(writer, result)).WrittenSpan),
    };

    // Values are written directly or through MEAI's AOT-annotated resolver; nothing uses reflection.
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case JsonElement element: element.WriteTo(writer); break;
            case string text: writer.WriteStringValue(text); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case int number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case double number: writer.WriteNumberValue(number); break;
            case float number: writer.WriteNumberValue(number); break;
            case decimal number: writer.WriteNumberValue(number); break;
            default:
                if (!AIJsonUtilities.DefaultOptions.TryGetTypeInfo(value.GetType(), out var typeInfo))
                    throw new InvalidOperationException($"Cannot serialize a tool value of type {value.GetType().FullName} for Anthropic.");
                JsonSerializer.Serialize(writer, value, typeInfo);
                break;
        }
    }

    private static JsonElement Json(Action<Utf8JsonWriter> write)
    {
        using var document = JsonDocument.Parse(Write(write).WrittenMemory);
        return document.RootElement.Clone();
    }

    private static ArrayBufferWriter<byte> Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        return buffer;
    }
}
