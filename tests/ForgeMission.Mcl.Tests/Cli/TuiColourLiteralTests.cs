using System.Text.RegularExpressions;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (53.6): themes are data. Every colour literal lives in Tui/ForgeTheme.cs, and the
// screen takes its styles from ForgeStyles only — it never names a theme or a colour.
public sealed partial class TuiColourLiteralTests
{
    [Fact]
    public void Colour_literals_appear_only_in_ForgeTheme()
    {
        var offenders = TuiSources()
            .Where(file => Path.GetFileName(file) != "ForgeTheme.cs")
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, number: index + 1)))
            .Where(item => ColourLiteral().IsMatch(item.line))
            .Select(item => $"{Path.GetFileName(item.file)}:{item.number}: {item.line.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ChatScreen_never_names_a_theme()
    {
        var screen = File.ReadAllText(Path.Combine(CliSource(), "Tui", "ChatScreen.cs"));

        Assert.DoesNotContain("ForgeTheme", screen);
        Assert.DoesNotMatch(ColourLiteral(), screen);
    }

    [Fact]
    public void ForgeTheme_holds_the_colour_literals()
    {
        Assert.Matches(ColourLiteral(), File.ReadAllText(Path.Combine(CliSource(), "Tui", "ForgeTheme.cs")));
    }

    [GeneratedRegex(@"Color\.(Rgb|RgbA|Basic16|Indexed256)\b|\bColors\.|\bConsoleColor\.|#[0-9a-fA-F]{6}\b")]
    private static partial Regex ColourLiteral();

    private static IEnumerable<string> TuiSources() =>
        Directory.EnumerateFiles(Path.Combine(CliSource(), "Tui"), "*.cs")
            .Append(Path.Combine(CliSource(), "ForgeConfig.cs"));

    private static string CliSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ForgeMission.Cli");
            if (Directory.Exists(Path.Combine(candidate, "Tui"))) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/ForgeMission.Cli from the test output.");
    }
}
