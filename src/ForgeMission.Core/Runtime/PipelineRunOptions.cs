using Microsoft.Extensions.AI;

namespace ForgeMission.Core.Runtime;

public record PipelineRunOptions(
    string MissionName,
    IReadOnlyDictionary<string, string>? Vars = null,
    // Mission names, root to current, for the currently executing mission.
    // Null at a fresh top-level call — the runner defaults it to [MissionName]; CreateChildOptions
    // appends the child's declared mission name for a nested sub-mission invocation.
    IReadOnlyList<string>? MissionPath = null,
    // Awaited pipeline lifecycle sink. Always awaited with the run cancellation token — a
    // throwing sink fails the run rather than silently dropping a fact. No caller is required to
    // supply it; without it a run emits no trace facts.
    Func<PipelineTraceEvent, CancellationToken, Task>? OnTrace = null,
    // Explicit opt-in for a root-scoped, generic tool pause. This declaration scope is retained
    // by Core while declared child missions run; a child receives neither a dispatcher
    // nor a capability handle. A call pauses the root run and must be resumed through
    // PipelineRunner.ResumeAsync.
    IList<AITool>? RootTools = null)
{
    /// <summary>Runtime-only verified segment storage, inherited by child missions.</summary>
    public PipelineExecutionWorkspace? ExecutionWorkspace { get; init; }
    /// <summary>Streams each tool-free, non-judge llm step and emits its text as
    /// <see cref="PipelineStepDelta"/> facts through <see cref="OnTrace"/>. Set only by
    /// the durable executor; a streamed step's result is its plain text with status pass. Judges and
    /// steps with tools attached keep the non-streaming path. Inherited by child missions.</summary>
    public bool StreamLlmDeltas { get; init; }

    /// <summary>Earlier turns of a durable chat. Seeded into the root mission's step
    /// context (including a paused and resumed run), and makes the root mission's first declared
    /// parameter the first step's input; never inherited by child missions and
    /// never checkpointed — a caller passes it again on <see cref="PipelineRunner.ResumeAsync"/>.</summary>
    public ChatHistory? ChatHistory { get; init; }
}
