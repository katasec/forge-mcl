using System.Text.RegularExpressions;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (53.6, Phase 56 G7): themes are data. Every colour literal lives in Tui/ForgeTheme.cs,
// and the screen takes its styles from ForgeStyles only — it never names a theme or a colour. The scan
// covers every file under Tui/ (Graphics included) and also flags raw RGB/RGBA byte literals. One
// named colour exception: KittyImages, whose Color.Rgb carries an image id, not a visual colour.
// One raw stdout writer: RawStdout, which writes the kitty image and caret-colour escapes (XenoAtom's
// writer does not reach the terminal while the app runs); every other file goes through it.
public sealed partial class TuiColourLiteralTests
{
    private const string ImageIdFile = "KittyImages.cs";
    private const string RawStdoutFile = "RawStdout.cs";

    [Fact]
    public void Colour_literals_appear_only_in_ForgeTheme()
    {
        var offenders = TuiLines()
            .Where(item => Path.GetFileName(item.File) != "ForgeTheme.cs")
            .Where(item => IsColourLiteral(Path.GetFileName(item.File), item.Line))
            .Select(item => $"{Path.GetRelativePath(CliSource(), item.File)}:{item.Number}: {item.Line.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_image_id_exception_covers_only_the_id_colour()
    {
        var lines = File.ReadLines(Path.Combine(CliSource(), "Tui", "Graphics", ImageIdFile)).Where(line => ColourLiteral().IsMatch(line)).ToList();

        Assert.Single(lines);
        Assert.Contains("IdColor(uint id) => Color.Rgb((byte)(id >> 16), (byte)(id >> 8), (byte)id)", lines[0]);
    }

    [Theory]
    [InlineData("Color.Rgb(0x10, 0x1d, 0x34)")]
    [InlineData("Colors.Red")]
    [InlineData("var x = \"#a1b2c3\";")]
    [InlineData("new Rgb(16, 29, 52)")]
    [InlineData("Shadow = new(0x10, 0x1d, 0x34, 13),")]
    [InlineData("Paint((0x10, 0x1d, 0x34))")]
    [InlineData("// --shadow: 0 1px 2px rgba(16,29,52,.05)")]
    public void The_scan_catches_colour_and_raw_rgba_literals(string line)
    {
        Assert.True(IsColourLiteral("Sample.cs", line));
    }

    [Theory]
    [InlineData("Padding = new Thickness(1, 0, 1, 0),")]
    [InlineData("private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47];")]
    [InlineData("var (r, g, b) = Srgb.ToLinear(fill);")]
    public void The_scan_leaves_layout_numbers_and_byte_tables_alone(string line)
    {
        Assert.False(IsColourLiteral("Sample.cs", line));
    }

    [Fact]
    public void Only_RawStdout_writes_to_stdout()
    {
        var writers = TuiLines()
            .Where(item => StdoutWriter().IsMatch(item.Line))
            .Where(item => !(Path.GetFileName(item.File) == RawStdoutFile && item.Line.Contains("OpenStandardOutput", StringComparison.Ordinal)))
            .Select(item => $"{Path.GetRelativePath(CliSource(), item.File)}:{item.Number}: {item.Line.Trim()}")
            .ToList();

        Assert.Empty(writers);
        Assert.Single(TuiLines(), item => Path.GetFileName(item.File) == RawStdoutFile && item.Line.Contains("OpenStandardOutput", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Console.Out.Write(x);")]
    [InlineData("Console.Write(x);")]
    [InlineData("Console.WriteLine(x);")]
    [InlineData("Terminal.Write(x);")]
    [InlineData("using var stdout = Console.OpenStandardOutput();")]
    public void The_stdout_scan_catches_every_writer(string line)
    {
        Assert.Matches(StdoutWriter(), line);
    }

    /// <summary>Phase 56 G2: StbTrueTypeSharp, and the unsafe code its API needs, stay in GlyphText.</summary>
    [Fact]
    public void Only_GlyphText_uses_StbTrueTypeSharp_or_unsafe_code()
    {
        var cli = Directory.EnumerateFiles(CliSource(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        // Code only: comments may name the package.
        var users = cli.Where(file => File.ReadLines(file).Any(line => StbOrUnsafe().IsMatch(line.Split("//")[0])))
            .Select(file => Path.GetRelativePath(CliSource(), file)).ToList();

        Assert.Equal([Path.Combine("Tui", "Graphics", "GlyphText.cs")], users);
    }

    /// <summary>Phase 56 Task 5, the XenoCells Type-2 exception: only XenoCells reaches into
    /// XenoAtom's internals ([UnsafeAccessor]); anything else must use the public API.</summary>
    [Fact]
    public void Only_XenoCells_uses_UnsafeAccessor()
    {
        var users = Directory.EnumerateFiles(CliSource(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => File.ReadLines(file).Any(line => line.Split("//")[0].Contains("UnsafeAccessor", StringComparison.Ordinal)))
            .Select(file => Path.GetRelativePath(CliSource(), file)).ToList();

        Assert.Equal([Path.Combine("Tui", "XenoCells.cs")], users);
    }

    [GeneratedRegex(@"\bStbTrueType|\bunsafe\b|\bfixed\s*\(")]
    private static partial Regex StbOrUnsafe();

    private static bool IsColourLiteral(string fileName, string line) =>
        RawRgba().IsMatch(line) || (ColourLiteral().IsMatch(line) && fileName != ImageIdFile);

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

    /// <summary>CSS rgb()/rgba(), three hex byte literals in parentheses, or a constructor
    /// (target-typed or Rgb*) taking three decimal byte literals.</summary>
    [GeneratedRegex(@"\brgba?\s*\(|\(\s*0x[0-9a-fA-F]{1,2}\s*,\s*0x[0-9a-fA-F]{1,2}\s*,\s*0x[0-9a-fA-F]{1,2}\b|\bnew\s*(Rgb\w*\s*)?\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\b")]
    private static partial Regex RawRgba();

    [GeneratedRegex(@"\bConsole\.(Out\b|Write|OpenStandardOutput)|\bTerminal\.Write")]
    private static partial Regex StdoutWriter();

    private static IEnumerable<(string File, string Line, int Number)> TuiLines() =>
        TuiSources().SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, index + 1)));

    private static IEnumerable<string> TuiSources() =>
        Directory.EnumerateFiles(Path.Combine(CliSource(), "Tui"), "*.cs", SearchOption.AllDirectories)
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
