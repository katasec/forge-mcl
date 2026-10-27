using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge list experts prints each expert with its input -> output on stdout, or says the directory
// has none; both exit 0.
public sealed class ListExpertsTests
{
    [Fact]
    public async Task Lists_each_expert_with_its_input_and_output()
    {
        await using var harness = ForgeCliHarness.Start();
        var experts = Path.Combine(AppContext.BaseDirectory, "Missions", "noop", "experts");

        var result = await harness.RunAsync("list", "experts", "--experts", experts);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"Experts in {experts}:\n  {"PassThrough",-30} any text -> same text\n",
            result.StandardOutput.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Says_when_the_directory_has_no_experts()
    {
        await using var harness = ForgeCliHarness.Start();
        var experts = Directory.CreateDirectory(Path.Combine(harness.Home, "experts")).FullName;

        var result = await harness.RunAsync("list", "experts", "--experts", experts);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"No experts found in {experts}\n", result.StandardOutput.ReplaceLineEndings("\n"));
    }
}
