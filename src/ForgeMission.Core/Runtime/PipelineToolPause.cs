using System.Text.Json;

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
