using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge clean deletes the expert and mission caches under ~/.forge and reports how many files they
// held, counted through every subfolder.
public sealed class CleanCommandTests
{
    [Fact]
    public async Task Clean_removes_the_expert_and_mission_caches_and_reports_every_nested_file()
    {
        await using var harness = ForgeCliHarness.Start();
        var forge = Path.Combine(harness.Home, ".forge");
        foreach (var file in new[] { "experts/ghcr.io/a/1/expert.md", "experts/registry.test/c/3/expert.md", "missions/ghcr.io/m/1/mission.mcl" })
            Write(Path.Combine(forge, file));

        var result = await harness.RunAsync("clean");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Removed 3 cached file(s) from the experts and missions caches.\n", result.StandardOutput.ReplaceLineEndings("\n"));
        Assert.False(Directory.Exists(Path.Combine(forge, "experts")));
        Assert.False(Directory.Exists(Path.Combine(forge, "missions")));
    }

    [Fact]
    public async Task Clean_with_no_cache_reports_nothing_to_clean()
    {
        await using var harness = ForgeCliHarness.Start();

        var result = await harness.RunAsync("clean");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Nothing to clean (no cached experts or missions).\n", result.StandardOutput.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Clean_rejects_a_registry_that_escapes_the_cache()
    {
        await using var harness = ForgeCliHarness.Start();
        var kept = Path.Combine(harness.Home, ".forge", "credentials.json");
        Write(kept);

        var result = await harness.RunAsync("clean", "--registry", "..");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must be a single host name", result.StandardError);
        Assert.True(File.Exists(kept));
    }

    static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }
}
