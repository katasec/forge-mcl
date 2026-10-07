using ForgeMission.ClientRuntime;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;
using ForgeMission.Core.Tools;
using Microsoft.Extensions.AI;
using MclProgram = ForgeMission.Parser.Program;

namespace ForgeMission.Cli;

// CLI composition only: Core reasons and checkpoints; Hands owns local capability authority.
internal static class ForgeRun
{
    // Ownership transfers to this method; it disposes Hands on every exit, including exceptions.
    internal static async Task<MissionResult> RunAsync(
        MclProgram ast, Dictionary<string, ExpertDefinition> experts, PipelineRunner runner,
        PipelineRunOptions options, ClientExecutionSession hands, CancellationToken ct)
    {
        await using (hands)
        {
            var toolOptions = options with { RootTools = hands.ToolDeclarations.ToList() };
            var executors = new ToolExecutorRegistry();
            var result = await runner.RunAsync(ast, experts, toolOptions, ct);
            while (result.Pause is { } pause)
            {
                ct.ThrowIfCancellationRequested();
                var reply = await ExecuteToolAsync(pause.ToolCall, executors, hands, ct);
                result = await runner.ResumeAsync(ast, experts,
                    new PipelineResumeRequest(pause.Continuation, reply), toolOptions, ct);
            }
            return result;
        }
    }

    internal static ClientExecutionSession CreateHands(string root, CancellationToken ct) =>
        ClientExecutionSession.CreateForMission(root, MissionExecutionProfile.ProjectWorkspace,
            new CapabilityAuthorizationPolicy([
                new("file", new CapabilityAuthorizationRule(AuthorizationOutcome.AutoApproved))]),
            new RejectConfirmation(), ct);

    private static async Task<PipelineToolResult> ExecuteToolAsync(
        PipelineToolCall call, ToolExecutorRegistry executors, ClientExecutionSession hands,
        CancellationToken ct)
    {
        var arguments = call.Arguments.EnumerateObject().ToDictionary(
            property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        var result = await executors.ExecuteAsync(new FunctionCallContent(call.CallId, call.Name, arguments), hands, ct);
        return new PipelineToolResult(call.CallId,
            result.IsError ? PipelineToolResultStatus.Failed : PipelineToolResultStatus.Succeeded, result.Content);
    }

    private sealed class RejectConfirmation : ICapabilityConfirmationHandler
    {
        public Task<bool> ConfirmAsync(CapabilityConfirmationRequest request, CancellationToken ct) =>
            Task.FromResult(false);
    }
}
