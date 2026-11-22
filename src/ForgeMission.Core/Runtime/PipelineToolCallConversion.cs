using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ForgeMission.Core.Runtime;

/// <summary>
/// Converts a provider-SDK FunctionCallContent to the closed PipelineToolCall shape — no
/// provider SDK object crosses into the trace. A small Utf8JsonWriter conversion keeps it
/// reflection-free for Native AOT.
/// </summary>
internal static class PipelineToolCallConversion
{
    public static PipelineToolCall From(FunctionCallContent call)
        => new(call.CallId, call.Name, ToolArgumentsToJsonElement(call.Arguments));

    private static JsonElement ToolArgumentsToJsonElement(IDictionary<string, object?>? arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        foreach (var (name, value) in arguments ?? new Dictionary<string, object?>())
        {
            writer.WritePropertyName(name);
            WriteToolArgumentValue(writer, value);
        }
        writer.WriteEndObject();
        writer.Flush();

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteToolArgumentValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                return;
            case long integer:
                writer.WriteNumberValue(integer);
                return;
            case double number:
                writer.WriteNumberValue(number);
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported tool argument type '{value.GetType().FullName}'.");
        }
    }
}
