using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge validate with an mcl.lock: an expert that fails to load is reported on stderr with every
// error it has, and validate exits 1 — for one error and for several.
public sealed class ValidateExpertLoadFailureTests
{
    private const string LockFile = "version: 1\nexperts:\n  Writer:\n    source: local\n    path: experts/Writer/expert.md\n";

    [Theory]
    [InlineData("---\nname: Writer\ninput: [\n---\nbody\n", "expert.md:3:8")]
    [InlineData("---\nname: Writer\n---\nbody\n", "'input'", "'output'")]
    public async Task A_locked_expert_load_failure_is_reported_and_exits_1(string expertMd, params string[] expectedErrors)
    {
        await using var harness = ForgeCliHarness.Start();
        var mission = WriteLockedMission(harness.Home, expertMd);

        var result = await harness.RunAsync("validate", mission);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("Unhandled exception", result.StandardError);
        foreach (var expected in expectedErrors) Assert.Contains(expected, result.StandardError);
        Assert.Equal("", result.StandardOutput);
    }

    // Several locked experts failing: every expert's errors are reported, not just the first one's.
    [Fact]
    public async Task Every_failing_locked_expert_is_reported()
    {
        await using var harness = ForgeCliHarness.Start();
        var mission = WriteLockedMission(harness.Home, "---\nname: Writer\n---\nbody\n");
        File.AppendAllText(Path.Combine(harness.Home, "mcl.lock"),
            "  Reviewer:\n    source: local\n    path: experts/Reviewer/expert.md\n");

        var result = await harness.RunAsync("validate", mission);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("'input'", result.StandardError);
        Assert.Contains("MCL008", result.StandardError);
        Assert.Contains("'Reviewer'", result.StandardError);
    }

    private static string WriteLockedMission(string directory, string expertMd)
    {
        var expert = Directory.CreateDirectory(Path.Combine(directory, "experts", "Writer")).FullName;
        File.WriteAllText(Path.Combine(expert, "expert.md"), expertMd);
        File.WriteAllText(Path.Combine(directory, "mcl.lock"), LockFile);
        var mission = Path.Combine(directory, "mission.mcl");
        File.WriteAllText(mission, "mission Check(task) = {\n    Writer\n}\n");
        return mission;
    }
}
