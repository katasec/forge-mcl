using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge provider scaffold <name> prints a ready-to-paste forge.toml block for every provider
// ChatClients builds, xai included.
public sealed class ProviderScaffoldTests
{
    [Fact]
    public async Task Scaffold_xai_prints_an_xai_provider_block()
    {
        await using var harness = ForgeCliHarness.Start();

        var result = await harness.RunAsync("provider", "scaffold", "xai");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("provider = \"xai\"", result.StandardOutput);
        Assert.Contains("apiKey   = env(\"XAI_API_KEY\")", result.StandardOutput);
    }

    [Fact]
    public async Task List_prints_xai_last()
    {
        await using var harness = ForgeCliHarness.Start();

        var result = await harness.RunAsync("provider", "list");

        Assert.Equal(0, result.ExitCode);
        var names = result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ')[0])
            .Where(name => name is "openai" or "anthropic" or "azure" or "ollama" or "xai");
        Assert.Equal(["openai", "anthropic", "azure", "ollama", "xai"], names);
    }
}
