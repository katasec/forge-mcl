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

    internal static bool IsRuntimeDirectory(string name) => name is WorkDir or InputDir or OutputDir;

    internal static bool IsReserved(string name) => name is Output or Feedback or Attempt or MaxLoops
        or History or Conversation or ApiKey or Model or Provider or Endpoint
        || IsRuntimeDirectory(name) || name.StartsWith("__", StringComparison.Ordinal)
        || name.StartsWith("FORGE_", StringComparison.Ordinal);
}
