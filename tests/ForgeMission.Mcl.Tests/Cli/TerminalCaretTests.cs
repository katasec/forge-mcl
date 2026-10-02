using System.Reflection;
using XenoAtom.Terminal.UI;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (Phase 56 Task 3 review): while the TUI runs the terminal cursor (the composer
// caret) takes the theme's Caret token (OSC 12), and the user's own cursor colour is restored
// (OSC 112) on every way out: a normal quit, the G8 stop (the TUI returns normally) and an
// exception. Read from forge.dll through reflection like the other CLI tests.
public sealed class TerminalCaretTests
{
    private const string Restore = "\u001b]112\u001b\\";

    private static readonly Assembly Forge = LoadForge();
    private static readonly Type ThemeType = Type("ForgeMission.Cli.Tui.ForgeTheme");
    private static readonly Type CaretType = Type("ForgeMission.Cli.Tui.TerminalCaret");

    [Fact]
    public void The_caret_is_accent_on_light_and_prompt_on_dark()
    {
        Assert.Equal(Token("Light", "Accent"), Token("Light", "Caret"));
        Assert.Equal(Token("Dark", "Prompt"), Token("Dark", "Caret"));
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void The_styles_carry_the_caret_token(string theme)
    {
        var stylesType = Type("ForgeMission.Cli.Tui.ForgeStyles");
        var styles = Activator.CreateInstance(stylesType, Theme(theme))!;

        Assert.Equal(Token(theme, "Caret"), (Color)stylesType.GetProperty("Caret")!.GetValue(styles)!);
    }

    [Fact]
    public void The_set_escape_is_osc_12_with_the_token()
    {
        Assert.Equal("\u001b]12;rgb:0f/6f/eb\u001b\\", SetEscape(Token("Light", "Caret")));
    }

    [Fact]
    public async Task A_run_that_ends_normally_restores_the_cursor_colour()
    {
        var written = new List<string>();

        await WhileRunning(Token("Light", "Caret"), () => Task.CompletedTask, written.Add);

        Assert.Equal([SetEscape(Token("Light", "Caret")), Restore], written);
    }

    [Fact]
    public async Task A_run_that_throws_restores_the_cursor_colour_and_still_throws()
    {
        var written = new List<string>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WhileRunning(Token("Dark", "Caret"), () => throw new InvalidOperationException("boom"), written.Add));

        Assert.Equal("boom", error.Message);
        Assert.Equal([SetEscape(Token("Dark", "Caret")), Restore], written);
    }

    private static string SetEscape(Color caret) =>
        (string)CaretType.GetMethod("SetEscape", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [caret])!;

    private static Task WhileRunning(Color caret, Func<Task> run, Action<string> write)
    {
        var method = CaretType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(m => m.Name == "WhileRunning");
        return (Task)method.Invoke(null, [caret, run, write])!;
    }

    private static Color Token(string theme, string name) => (Color)ThemeType.GetProperty(name)!.GetValue(Theme(theme))!;

    private static object Theme(string name) => ThemeType.GetProperty(name, BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;

    private static Type Type(string name) => Forge.GetType(name, throwOnError: true)!;

    private static Assembly LoadForge()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ForgeMission.Cli", "bin", "Debug", "net10.0", "forge.dll");
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate built forge.dll for CLI reflection tests.");
    }
}
