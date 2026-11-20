namespace ForgeMission.Core.Runtime;

internal static class PipelineToolContinuationInstructions
{
    /// <summary>The resumed agent step's tool calls and results so far, in provider order; set only
    /// by PipelineRunner, added after the step's own input by DirectExpertRunner.</summary>
    public const string TurnMessages = "__pipeline_tool_turn_messages";

    /// <summary>Complete generic provider response messages accompanying the existing calls.
    /// Consumed by PipelineRunner at the invocation boundary, never retained in step context.</summary>
    public const string ResponseMessages = "__pipeline_tool_response_messages";
}
