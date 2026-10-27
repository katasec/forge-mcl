using System.Text.RegularExpressions;
using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// Commands given no path fall back to their default file in the current directory, resolved to a
// full path; in an empty directory each reports that full path and exits 1.
public sealed class DefaultPathTests
{
    [Theory]
    [InlineData("Cannot read file '", "mission.mcl", "init")]
    [InlineData("Cannot read file '", "mission.mcl", "validate")]
    [InlineData("Agent config not found: ", "agent.yaml", "serve")]
    [InlineData("Agent config not found: ", "agent.yaml", "agent", "start")]
    [InlineData("Agent config not found: ", "agent.yaml", "agent", "stop")]
    [InlineData("Agent config not found: ", "agent.yaml", "webui", "start")]
    [InlineData("No mission.mcl in ", "", "publish", "registry.test/pin/noop:1")]
    public async Task No_path_resolves_to_the_default_file_in_the_current_directory(string error, string file, params string[] command)
    {
        await using var harness = ForgeCliHarness.Start();
        var resolved = Path.Combine(Path.GetFileName(harness.Home), file);

        var result = await harness.RunAsync(command);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        var separator = Regex.Escape(Path.DirectorySeparatorChar.ToString());
        Assert.Matches(Regex.Escape(error) + separator + @"\S*" + separator + Regex.Escape(resolved), result.StandardError);
    }
}
