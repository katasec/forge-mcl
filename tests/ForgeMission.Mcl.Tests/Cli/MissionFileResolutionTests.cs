using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

public class MissionFileResolutionTests
{
    // forge claude with no target and no agent.yaml falls back to the .mcl files in the current
    // directory: none or several is an error naming what to do, exit 1. Needs no claude CLI.
    [Theory]
    [InlineData(0, "error: No mission found. Pass a .mcl path or run from a directory with a mission.\n")]
    [InlineData(2, "error: Multiple .mcl files here — pass the one you mean: forge claude ./<mission>.mcl\n")]
    public async Task Claude_without_a_target_needs_exactly_one_mcl_here(int missions, string expected)
    {
        await using var harness = ForgeCliHarness.Start();
        for (var i = 0; i < missions; i++)
            File.WriteAllText(Path.Combine(harness.WorkingDirectory, $"m{i}.mcl"), "");

        var result = await harness.RunAsync("claude", "--print-env");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.Equal(expected, result.StandardError.ReplaceLineEndings("\n"));
    }
}
