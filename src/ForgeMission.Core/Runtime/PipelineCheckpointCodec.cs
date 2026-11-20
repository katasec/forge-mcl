using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace ForgeMission.Core.Runtime;

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
