using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// A mission file that exists but cannot be read is reported once on stderr and exits 1, for every
// command that reads the mission itself. A directory named mission.mcl stands in for an unreadable file.
public sealed class MissionReadFailureTests
{
    [Theory]
    [InlineData("init")]
    [InlineData("validate")]
    [InlineData("mcp")]
    public async Task An_unreadable_mission_is_reported_and_exits_1(string command)
    {
        await using var harness = ForgeCliHarness.Start();
        var mission = Directory.CreateDirectory(Path.Combine(harness.Home, "mission.mcl")).FullName;
        File.WriteAllText(Path.Combine(harness.Home, "mcl.lock"), "");

        var result = await harness.RunAsync(command, mission);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Cannot read file '{mission}'", result.StandardError);
        Assert.Equal("", result.StandardOutput);
    }
}
