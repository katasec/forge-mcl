using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace ForgeMission.Core.Runtime;

/// <summary>Versioned opaque Core checkpoint. Consumers persist and correlate the payload only.</summary>
public sealed record PipelineContinuation(int FormatVersion, string Payload);

/// <summary>One generic tool request at a root-scoped pause. It carries no executor or authority.</summary>
public sealed record PipelineToolPause(
    string RootMissionName,
    IReadOnlyList<string> MissionPath,
    string ExpertName,
    int Attempt,
    PipelineToolCall ToolCall,
    PipelineContinuation Continuation);

public enum PipelineToolResultStatus { Succeeded, Denied, Cancelled, Failed }

/// <summary>Correlated result supplied by the owner of tool policy and execution, never Core.</summary>
public sealed record PipelineToolResult(string CallId, PipelineToolResultStatus Status, string? Content = null);

/// <summary>One opaque continuation plus the exactly correlated external result.</summary>
public sealed record PipelineResumeRequest(PipelineContinuation Continuation, PipelineToolResult Result);

public enum PipelineFailure
{
    UnsupportedTool,
    MultipleOutstandingTools,
    InvalidContinuation,
    DuplicateContinuation,
    LateContinuation,
}

/// <summary>Closed declaration preserved in the opaque checkpoint so Core can re-declare tools.</summary>
public sealed record PipelineToolDeclaration(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// The replay checkpoint (inner format 3). A resume runs the mission from the top: each step whose
/// key is in <see cref="Log"/> returns its recorded result instead of running, until the step at
/// <see cref="PausedKey"/> continues its tool turn.
/// </summary>
internal sealed record PipelineContinuationCheckpoint(
    int FormatVersion,
    string SessionId,
    string RootExecutionId,
    int ContinuationOrdinal,
    string RootMissionName,
    string RootDefinitionFingerprint,
    string RootToolScopeFingerprint,
    IReadOnlyList<PipelineToolDeclaration> ToolDeclarations,
    IReadOnlyList<string> MissionPath,
    string ExpertName,
    int Attempt,
    IReadOnlyList<ChatMessage> TurnMessages,
    IReadOnlyDictionary<string, string> RootInputs,
    IReadOnlyList<string> AdmittedInputNames,
    IReadOnlyList<PipelineStepLogEntry> Log,
    string PausedKey);

/// <summary>One completed step: its envelope and the context keys it wrote. Binding and environment
/// values are never here; they are re-derived on replay.</summary>
internal sealed record PipelineStepLogEntry(
    string Key,
    string Text,
    string Status,
    string? Reason,
    IReadOnlyDictionary<string, PipelineLoggedValue> Writes);

/// <summary>A step-written context value, typed so a double replays as a double.</summary>
internal sealed record PipelineLoggedValue(string? Text, double? Number)
{
    public object ToContextValue() => Number is { } number ? number : Text ?? string.Empty;

    public static PipelineLoggedValue? From(object value) => value switch
    {
        string text => new PipelineLoggedValue(text, null),
        double number => new PipelineLoggedValue(null, number),
        _ => null,
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(PipelineContinuationCheckpoint))]
internal partial class PipelineContinuationJsonContext : JsonSerializerContext;

internal static class PipelineCheckpointCodec
{
    // MEAI's own AOT-safe metadata covers the turn's ChatMessages (tool calls and results); this
    // context covers the checkpoint around them.
    private static readonly JsonTypeInfo<PipelineContinuationCheckpoint> CheckpointInfo = CreateCheckpointInfo();

    internal static string Write(PipelineContinuationCheckpoint checkpoint) =>
        JsonSerializer.Serialize(checkpoint, CheckpointInfo);

    /// <summary>The envelope version consumers carry; the payload stays opaque to them.</summary>
    internal const int EnvelopeVersion = 1;

    /// <summary>The checkpoint format inside the payload. Formats 1 and 2 are rejected.</summary>
    internal const int CheckpointVersion = 3;

    internal static bool TryRead(PipelineContinuation continuation, out PipelineContinuationCheckpoint checkpoint)
    {
        checkpoint = null!;
        if (continuation.FormatVersion != EnvelopeVersion || continuation.Payload is null) return false;
        try
        {
            checkpoint = JsonSerializer.Deserialize(continuation.Payload, CheckpointInfo)!;
            return checkpoint is not null && ValidIdentity(checkpoint)
                && ValidCollections(checkpoint) && ValidMessages(checkpoint.TurnMessages);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The call the checkpoint waits on: the turn's last message holds exactly one.</summary>
    internal static FunctionCallContent PendingCall(PipelineContinuationCheckpoint checkpoint) =>
        checkpoint.TurnMessages[^1].Contents.OfType<FunctionCallContent>().Single();

    private static bool ValidIdentity(PipelineContinuationCheckpoint checkpoint) =>
        checkpoint.FormatVersion == CheckpointVersion && checkpoint.ContinuationOrdinal > 0 && checkpoint.Attempt > 0
        && !string.IsNullOrWhiteSpace(checkpoint.SessionId) && !string.IsNullOrWhiteSpace(checkpoint.RootExecutionId)
        && !string.IsNullOrWhiteSpace(checkpoint.RootMissionName) && !string.IsNullOrWhiteSpace(checkpoint.ExpertName)
        && !string.IsNullOrWhiteSpace(checkpoint.RootDefinitionFingerprint)
        && !string.IsNullOrWhiteSpace(checkpoint.RootToolScopeFingerprint) && !string.IsNullOrWhiteSpace(checkpoint.PausedKey);

    private static bool ValidCollections(PipelineContinuationCheckpoint checkpoint) =>
        checkpoint.RootInputs is not null && checkpoint.RootInputs.All(pair => pair.Value is not null)
        && checkpoint.AdmittedInputNames is not null && checkpoint.AdmittedInputNames.All(name => !string.IsNullOrWhiteSpace(name))
        && checkpoint.MissionPath is not null && checkpoint.MissionPath.All(name => !string.IsNullOrWhiteSpace(name))
        && checkpoint.ToolDeclarations is not null && checkpoint.ToolDeclarations.All(ValidTool)
        && checkpoint.Log is not null && checkpoint.Log.All(ValidLog);

    private static bool ValidTool(PipelineToolDeclaration? tool) => tool is not null
        && !string.IsNullOrWhiteSpace(tool.Name) && tool.Description is not null
        && tool.InputSchema.ValueKind != JsonValueKind.Undefined;

    private static bool ValidLog(PipelineStepLogEntry? entry) => entry is not null
        && !string.IsNullOrWhiteSpace(entry.Key) && entry.Text is not null && entry.Status is not null
        && entry.Writes is not null && entry.Writes.All(pair => pair.Value is not null);

    private static bool ValidMessages(IReadOnlyList<ChatMessage>? messages)
    {
        if (messages is not { Count: > 0 } || messages.Any(message => message is null || message.Contents is null)) return false;
        var calls = messages[^1].Contents.OfType<FunctionCallContent>().ToArray();
        return calls.Length == 1 && !string.IsNullOrWhiteSpace(calls[0].CallId) && !string.IsNullOrWhiteSpace(calls[0].Name);
    }

    private static JsonTypeInfo<PipelineContinuationCheckpoint> CreateCheckpointInfo()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.TypeInfoResolverChain.Insert(0, new PipelineContinuationJsonContext(new JsonSerializerOptions()));
        return (JsonTypeInfo<PipelineContinuationCheckpoint>)options.GetTypeInfo(typeof(PipelineContinuationCheckpoint));
    }
}

internal static class PipelineToolContinuationInstructions
{
    /// <summary>The resumed agent step's tool calls and results so far, in provider order; set only
    /// by PipelineRunner, added after the step's own input by DirectExpertRunner.</summary>
    public const string TurnMessages = "__pipeline_tool_turn_messages";

    /// <summary>Complete generic provider response messages accompanying the existing calls.
    /// Consumed by PipelineRunner at the invocation boundary, never retained in step context.</summary>
    public const string ResponseMessages = "__pipeline_tool_response_messages";
}
