using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// forge list experts prints each expert with its input -> output on stdout, or says the directory
// has none; both exit 0.
public sealed class ListExpertsTests
{
    [Fact]
    public async Task Lists_each_expert_with_its_input_and_output()
    {
        await using var harness = ForgeCliHarness.Start();
        var experts = Path.Combine(AppContext.BaseDirectory, "Missions", "noop", "experts");

        var result = await harness.RunAsync("list", "experts", "--experts", experts);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"Experts in {experts}:\n  {"PassThrough",-30} any text -> same text\n",
            result.StandardOutput.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Says_when_the_directory_has_no_experts()
    {
        await using var harness = ForgeCliHarness.Start();
        var experts = Directory.CreateDirectory(Path.Combine(harness.Home, "experts")).FullName;

        var result = await harness.RunAsync("list", "experts", "--experts", experts);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"No experts found in {experts}\n", result.StandardOutput.ReplaceLineEndings("\n"));
    }

    // An expert with a frontmatter error is reported against its own source line, with a caret
    // under the field; the CRLF line ending does not leak into the excerpt. Exit 1.
    [Fact]
    public async Task Reports_an_expert_error_against_its_source_line()
    {
        await using var harness = ForgeCliHarness.Start();
        var experts = Directory.CreateDirectory(Path.Combine(harness.Home, "experts", "Broken")).Parent!.FullName;
        var expertFile = Path.Combine(experts, "Broken", "expert.md");
        File.WriteAllText(expertFile, "---\r\nname: Broken\r\ninput:\r\noutput: text\r\n---\r\nbody\r\n");

        var result = await harness.RunAsync("list", "experts", "--experts", experts);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.DoesNotContain("input:\r", result.StandardError);
        Assert.Equal($"""
            error: Missing required frontmatter field 'input' in expert.md
              --> {expertFile}:3:1
                |
            3   | input:
                | ^^^^^
                |

            """, result.StandardError.ReplaceLineEndings("\n"));
    }
}
