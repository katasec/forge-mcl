namespace ForgeMission.Core.Runtime;

/// <summary>
/// The one owner of the reserved context-bag keys: the keys the runtime writes or reads itself,
/// which a mission parameter or durable input may not take.
/// </summary>
internal static class ContextKeys
{
    // Runtime keys written by the pipeline runner and expert runners.
    internal const string Output       = "output";
    internal const string Feedback     = "feedback";
    internal const string Attempt      = "attempt";
    internal const string MaxLoops     = "max_loops";
    internal const string History      = "history";
    internal const string Conversation = "conversation";
    internal const string Tools        = "tools";
    internal const string ToolCalls    = "tool_calls";

    // Provider keys a mission may set to choose its model.
    internal const string ApiKey   = "apiKey";
    internal const string Model    = "model";
    internal const string Provider = "provider";
    internal const string Endpoint = "endpoint";

    // Directories an exec expert receives.
    internal const string WorkDir   = "work_dir";
    internal const string InputDir  = "input_dir";
    internal const string OutputDir = "output_dir";

    // One-shot instructions the runner passes to an expert runner and removes before the next step.
    // The "__" prefix keeps them out of mission authoring (see IsReserved).

    /// <summary>bool mirroring <see cref="Microsoft.Extensions.AI.ChatOptions.AllowMultipleToolCalls"/>.
    /// Set to false on an agent expert's call when root tools are attached: one tool call per pause.</summary>
    internal const string AllowMultipleToolCalls = "__pipeline_allow_multiple_tool_calls";

    /// <summary>The resumed agent step's tool calls and results so far, in provider order; set only
    /// by PipelineRunner, added after the step's own input by DirectExpertRunner.</summary>
    internal const string ToolTurnMessages = "__pipeline_tool_turn_messages";

    /// <summary>Complete provider response messages accompanying the existing calls. Consumed by
    /// PipelineRunner at the invocation boundary, never retained in step context.</summary>
    internal const string ToolResponseMessages = "__pipeline_tool_response_messages";

    /// <summary>The earlier turns of a durable chat (<see cref="Runtime.ChatHistory"/>).</summary>
    internal const string ChatHistory = "__chat_history";

    internal static bool IsRuntimeDirectory(string name) => name is WorkDir or InputDir or OutputDir;

    internal static bool IsReserved(string name) => name is Output or Feedback or Attempt or MaxLoops
        or History or Conversation or ApiKey or Model or Provider or Endpoint
        || IsRuntimeDirectory(name) || name.StartsWith("__", StringComparison.Ordinal)
        || name.StartsWith("FORGE_", StringComparison.Ordinal);
}
