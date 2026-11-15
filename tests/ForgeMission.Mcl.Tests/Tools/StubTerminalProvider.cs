using ForgeMission.Core.Tools;

namespace ForgeMission.Tests.Tools;

/// <summary>A terminal capability that runs nothing: registers "terminal" so the registry
/// declares the Bash tool.</summary>
internal sealed class StubTerminalProvider : ITerminalProvider
{
    public string CapabilityName => "terminal";

    public Task<ToolExecutionResult> ExecuteAsync(string command, string? workingDir = null, CancellationToken ct = default) =>
        Task.FromResult(ToolExecutionResult.Error("No terminal in this test."));
}
