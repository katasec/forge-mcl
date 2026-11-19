using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge init has no --refresh option: the expert cache is content-addressed by manifest digest, so
// there is nothing to re-pull. The option is rejected rather than silently ignored.
public sealed class InitCommandTests
{
    [Fact]
    public async Task Init_rejects_the_removed_refresh_option()
    {
        await using var harness = ForgeCliHarness.Start();

        var result = await harness.RunAsync("init", "--refresh");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--refresh", result.StandardError);
        Assert.False(File.Exists(Path.Combine(harness.Home, "mcl.lock")));
    }
}
