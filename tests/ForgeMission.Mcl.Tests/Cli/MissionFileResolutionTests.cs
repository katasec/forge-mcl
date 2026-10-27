using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

/// <summary>
/// Tests for the mission file defaulting behaviour in the CLI.
/// These guard against NullReferenceException when no file argument is supplied
/// and FileInfo.DirectoryName is derived from the resolved path.
/// </summary>
public class MissionFileResolutionTests
{
    // Mirrors the ResolveMission helper in Program.cs exactly
    private static FileInfo ResolveMission(FileInfo? arg)
        => new FileInfo(Path.GetFullPath(arg?.FullName ?? "mission.mcl"));

    [Fact]
    public void NoArg_ResolvesToMissionMcl()
    {
        var result = ResolveMission(null);
        Assert.Equal("mission.mcl", result.Name);
    }

    [Fact]
    public void NoArg_DirectoryNameIsNotNull()
    {
        // This is the exact invariant that caused the NullReferenceException.
        // new FileInfo("mission.mcl").DirectoryName returns null — Path.GetFullPath fixes it.
        var result = ResolveMission(null);
        Assert.NotNull(result.DirectoryName);
    }

    [Fact]
    public void NoArg_ResolvedToAbsolutePath()
    {
        var result = ResolveMission(null);
        Assert.True(Path.IsPathRooted(result.FullName));
    }

    [Fact]
    public void WithArg_UsesProvidedFile()
    {
        var provided = new FileInfo("/some/path/custom.fml");
        var result   = ResolveMission(provided);
        Assert.Equal("custom.fml", result.Name);
    }

    [Fact]
    public void WithArg_DirectoryNameIsNotNull()
    {
        var provided = new FileInfo("/some/path/custom.fml");
        var result   = ResolveMission(provided);
        Assert.NotNull(result.DirectoryName);
    }

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
