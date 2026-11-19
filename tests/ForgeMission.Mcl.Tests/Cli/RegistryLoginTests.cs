using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge registry login saves a token into ~/.forge/credentials.json. A credentials file that is not
// valid JSON is refused, not overwritten: every credential already in it would be lost.
public sealed class RegistryLoginTests
{
    [Fact]
    public async Task A_credentials_file_that_is_not_valid_json_is_left_untouched_and_reported()
    {
        await using var harness = ForgeCliHarness.Start();
        var credentials = Path.Combine(harness.Home, ".forge", "credentials.json");
        harness.WriteForgeFile("credentials.json", "{ not json");

        var result = await harness.RunAsync("registry", "login", "ghcr.io", "--token", "test-token");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"{credentials} is not valid JSON; fix or delete it.", result.StandardError);
        Assert.Equal("{ not json", File.ReadAllText(credentials));
    }
}
