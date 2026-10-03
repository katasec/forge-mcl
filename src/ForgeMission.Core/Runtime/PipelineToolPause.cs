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
/// The replay checkpoint (inner format 2). A resume runs the mission from the top: each step whose
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

    /// <summary>The checkpoint format inside the payload. Format 1 (frame snapshots) is rejected.</summary>
    internal const int CheckpointVersion = 2;

    internal static bool TryRead(PipelineContinuation continuation, out PipelineContinuationCheckpoint checkpoint)
    {
        checkpoint = null!;
        if (continuation.FormatVersion != EnvelopeVersion) return false;
        try
        {
            checkpoint = JsonSerializer.Deserialize(continuation.Payload, CheckpointInfo)!;
            return checkpoint is not null && checkpoint.FormatVersion == CheckpointVersion
                && !string.IsNullOrWhiteSpace(checkpoint.SessionId)
                && !string.IsNullOrWhiteSpace(checkpoint.PausedKey)
                && checkpoint.Log is not null
                && checkpoint.TurnMessages is { Count: > 0 }
                && checkpoint.TurnMessages[^1].Contents.OfType<FunctionCallContent>().Count() == 1;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The call the checkpoint waits on: the turn's last message holds exactly one.</summary>
    internal static FunctionCallContent PendingCall(PipelineContinuationCheckpoint checkpoint) =>
        checkpoint.TurnMessages[^1].Contents.OfType<FunctionCallContent>().Single();

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
}
