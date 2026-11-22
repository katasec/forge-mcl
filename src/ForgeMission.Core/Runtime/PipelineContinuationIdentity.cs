using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeMission.Core.Experts;
using ForgeMission.Parser;
using Microsoft.Extensions.AI;

namespace ForgeMission.Core.Runtime;

// Owns what makes a paused pipeline resumable: the checkpoint's identity rules, the root tool-scope
// fingerprint, and the resumed agent turn rebuilt from a checkpoint and its tool result.
internal static class PipelineContinuationIdentity
{
    // A checkpoint resumes only when its inputs, pending call, tool scope and mission definition
    // all still match what this runner was composed with. Clauses short-circuit cheapest first.
    internal static bool IsResumable(
        PipelineContinuationCheckpoint checkpoint,
        IReadOnlyList<string> admittedNames,
        string resultCallId,
        Program ast,
        Dictionary<string, ExpertDefinition> experts)
    {
        var pending = PipelineCheckpointCodec.PendingCall(checkpoint);
        return checkpoint.AdmittedInputNames.SequenceEqual(admittedNames, StringComparer.Ordinal)
            && checkpoint.RootInputs.Keys.All(name => admittedNames.Contains(name, StringComparer.Ordinal))
            && string.Equals(pending.CallId, resultCallId, StringComparison.Ordinal)
            && checkpoint.ToolDeclarations.Any(tool => string.Equals(tool.Name, pending.Name, StringComparison.Ordinal))
            && string.Equals(checkpoint.RootToolScopeFingerprint, ScopeFingerprint(checkpoint.ToolDeclarations), StringComparison.Ordinal)
            && string.Equals(checkpoint.RootDefinitionFingerprint,
                PipelineDefinitionFingerprint.Compute(ast, experts, checkpoint.RootMissionName), StringComparison.Ordinal);
    }

    // Pause hashes the declared schemas; resume hashes the checkpoint's re-serialized copies. Both
    // hash the same compact form so schema whitespace never invalidates a continuation.
    internal static string ScopeFingerprint(IEnumerable<PipelineToolDeclaration> declarations) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            declarations.Select(d => $"{d.Name}\u001f{d.Description}\u001f{CompactJson(d.InputSchema)}")))));

    internal static PipelineToolDeclaration ToDeclaration(AITool tool)
    {
        if (tool is not AIFunction function) throw new InvalidOperationException($"Root tool '{tool.Name}' must be an AIFunction declaration.");
        return new PipelineToolDeclaration(function.Name, function.Description ?? string.Empty, function.JsonSchema.Clone());
    }

    // The resumed agent's whole tool turn, as providers expect it resent: every earlier call and
    // result, the pending call, then this result.
    internal static IReadOnlyList<ChatMessage> ResumedTurn(PipelineContinuationCheckpoint checkpoint, PipelineToolResult result) =>
        [.. checkpoint.TurnMessages, new ChatMessage(ChatRole.Tool, [new FunctionResultContent(result.CallId, ToolResultText(result))])];

    private static string CompactJson(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) element.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string ToolResultText(PipelineToolResult result) => result.Status switch
    {
        PipelineToolResultStatus.Succeeded => result.Content ?? string.Empty,
        _ => $"ERROR [{result.Status}]: {result.Content ?? "Tool did not complete."}",
    };
}
