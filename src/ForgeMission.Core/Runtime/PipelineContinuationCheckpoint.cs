using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace ForgeMission.Core.Runtime;

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
