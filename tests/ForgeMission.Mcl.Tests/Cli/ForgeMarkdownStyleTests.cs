using System.Reflection;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Extensions.Markdown.Styling;
using XenoAtom.Terminal.UI.Styling;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (53.7): replies render Markdown in the theme's tokens. Every slot carries its
// token in both themes (a slot left at the package default would bring the package's own colours),
// and code blocks take the code-block tokens. The styles are read from forge.dll through reflection.
public sealed class ForgeMarkdownStyleTests
{
    private static readonly Assembly Forge = LoadForge();
    private static readonly Type ThemeType = Forge.GetType("ForgeMission.Cli.Tui.ForgeTheme", throwOnError: true)!;
    private static readonly Type StylesType = Forge.GetType("ForgeMission.Cli.Tui.ForgeStyles", throwOnError: true)!;

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Text_slots_carry_their_tokens(string theme)
    {
        var (tokens, md) = Load(theme);
        var strong = Fg(tokens, "TextStrong");

        Assert.Equal(strong, md.ParagraphStyle);
        foreach (var heading in new[] { md.Heading1Style, md.Heading2Style, md.Heading3Style, md.Heading4Style, md.Heading5Style, md.Heading6Style })
            Assert.Equal(strong | TextStyle.Bold, heading);
        Assert.Equal(strong | TextStyle.Bold, md.StrongStyle);
        Assert.Equal(Style.None | TextStyle.Italic, md.EmphasisStyle);
        Assert.Equal(Fg(tokens, "InlineCode"), md.InlineCodeStyle);
        Assert.Equal(Fg(tokens, "Link") | TextStyle.Underline, md.LinkStyle);
        Assert.Equal(Fg(tokens, "TextMuted"), md.QuotePrefixStyle);
        Assert.Equal(Fg(tokens, "TextMuted"), md.HtmlStyle);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Alerts_carry_their_tokens_on_the_card_surface(string theme)
    {
        var (tokens, md) = Load(theme);

        AssertAlert(md.NoteAlert, tokens, "Accent");
        AssertAlert(md.ImportantAlert, tokens, "Accent");
        AssertAlert(md.TipAlert, tokens, "Success");
        AssertAlert(md.WarningAlert, tokens, "Warning");
        AssertAlert(md.CautionAlert, tokens, "Error");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void No_colour_slot_is_left_at_the_package_default(string theme)
    {
        var (_, md) = Load(theme);
        // Emphasis is italic only by design (it keeps the surrounding text's colour), which is
        // also the package default; its exact value is asserted in Text_slots_carry_their_tokens.
        var slots = typeof(MarkdownStyle).GetProperties()
            .Where(slot => slot.PropertyType == typeof(Style) || slot.PropertyType == typeof(MarkdownAlertStyle))
            .Where(slot => slot.Name != nameof(MarkdownStyle.EmphasisStyle));

        var unchanged = slots.Where(slot => Equals(slot.GetValue(MarkdownStyle.Default), slot.GetValue(md))).Select(slot => slot.Name);

        Assert.Empty(unchanged);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Code_blocks_carry_the_code_block_tokens(string theme)
    {
        var tokens = Theme(theme);
        var codeBlock = StylesType.GetProperty("CodeBlock")!.GetValue(Styles(tokens))!;
        Style Slot(string name) => (Style)codeBlock.GetType().GetProperty(name)!.GetValue(codeBlock)!;

        // The border is drawn by the code-block ring (Phase 56 Task 3), from the shape below.
        Assert.Equal(Style.None.WithBackground(Token(tokens, "CodeBlockFill")), Slot("Fill"));
        Assert.Equal(Fg(tokens, "CodeBlockText").WithBackground(Token(tokens, "CodeBlockFill")), Slot("Text"));

        var shape = StylesType.GetProperty("CodeBlockShape")!.GetValue(Styles(tokens))!;
        Color Part(string name) => (Color)shape.GetType().GetProperty(name)!.GetValue(shape)!;
        Assert.Equal((Token(tokens, "CardSurface"), Token(tokens, "CodeBlockFill"), Token(tokens, "CodeBlockBorder")),
            (Part("Surface"), Part("Fill"), Part("Border")));
    }

    private static void AssertAlert(MarkdownAlertStyle alert, object tokens, string token)
    {
        Assert.Equal(Fg(tokens, token), alert.BorderStyle);
        Assert.Equal(Fg(tokens, token) | TextStyle.Bold, alert.TitleStyle);
        Assert.Equal(Style.None.WithBackground(Token(tokens, "CardSurface")), alert.BackgroundStyle);
    }

    private static (object Tokens, MarkdownStyle Markdown) Load(string theme)
    {
        var tokens = Theme(theme);
        return (tokens, (MarkdownStyle)StylesType.GetProperty("Markdown")!.GetValue(Styles(tokens))!);
    }

    private static object Styles(object tokens) => Activator.CreateInstance(StylesType, tokens)!;

    private static object Theme(string name) =>
        ThemeType.GetProperty(name, BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;

    private static Color Token(object tokens, string name) => (Color)ThemeType.GetProperty(name)!.GetValue(tokens)!;

    private static Style Fg(object tokens, string name) => Style.None.WithForeground(Token(tokens, name));

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
