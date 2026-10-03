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
    ProviderFailed,
}

/// <summary>Closed declaration preserved in the opaque checkpoint so Core can re-declare tools.</summary>
public sealed record PipelineToolDeclaration(string Name, string Description, JsonElement InputSchema);

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
    IReadOnlyList<PipelineExecutionFrame> Frames);

/// <summary>
/// A serializable activation record for the root-scoped interpreter.  ElementIndex is the next
/// declared element to run (or the paused agent element when ResumePausedAgent is true); parallel
/// fields record the branch that has not yet been completed.  These are execution facts, never
/// authority or provider objects.
/// </summary>
internal sealed record PipelineExecutionFrame(
    string MissionName,
    int ElementIndex,
    int Attempt,
    IReadOnlyDictionary<string, string> RuntimeDelta,
    IReadOnlyDictionary<string, PipelineEnvironmentBinding> EnvironmentBindings,
    string? LoopFeedback,
    bool AnyGuardedStepMatched,
    bool ResumePausedAgent,
    int? ParallelBranchIndex = null,
    IReadOnlyDictionary<string, string>? CompletedParallelOutputs = null,
    bool ReturnsToParallel = false);

/// <summary>AST-derived environment binding metadata. The value is intentionally never checkpointed.</summary>
internal sealed record PipelineEnvironmentBinding(string VariableName, string? DefaultValue);

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

    internal static bool TryRead(PipelineContinuation continuation, out PipelineContinuationCheckpoint checkpoint)
    {
        checkpoint = null!;
        if (continuation.FormatVersion != 1) return false;
        try
        {
            checkpoint = JsonSerializer.Deserialize(continuation.Payload, CheckpointInfo)!;
            return checkpoint is not null && checkpoint.FormatVersion == 1
                && !string.IsNullOrWhiteSpace(checkpoint.SessionId)
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
