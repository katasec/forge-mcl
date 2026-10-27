using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge clean deletes the expert cache under ~/.forge/experts and reports how many files it held,
// counted through every subfolder.
public sealed class CleanCommandTests
{
    [Fact]
    public async Task Clean_removes_the_expert_cache_and_reports_every_nested_file()
    {
        await using var harness = ForgeCliHarness.Start();
        var experts = Path.Combine(harness.Home, ".forge", "experts");
        foreach (var file in new[] { "ghcr.io/a/1/expert.md", "ghcr.io/b/2/expert.md", "registry.test/c/3/expert.md" })
        {
            var path = Path.Combine(experts, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }

        var result = await harness.RunAsync("clean");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"Removed 3 cached expert file(s) from {experts}\n", result.StandardOutput.ReplaceLineEndings("\n"));
        Assert.False(Directory.Exists(experts));
    }
}
