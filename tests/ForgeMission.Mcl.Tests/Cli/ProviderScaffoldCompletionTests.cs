using ForgeMission.ChatClients;
using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge provider scaffold <name> offers the known provider names as tab completions
// (System.CommandLine's [suggest] directive, which shell completion scripts call).
public sealed class ProviderScaffoldCompletionTests
{
    [Fact]
    public async Task Name_completes_to_the_known_providers()
    {
        await using var harness = ForgeCliHarness.Start();
        const string line = "provider scaffold ";

        var result = await harness.RunAsync($"[suggest:{line.Length}]", line);

        Assert.Equal(0, result.ExitCode);
        Assert.Subset(result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries).ToHashSet(),
            ProviderCatalog.Entries.Select(e => e.Name).ToHashSet());
    }
}
