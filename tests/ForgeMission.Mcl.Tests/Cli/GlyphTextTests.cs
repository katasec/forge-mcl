using System.Linq.Expressions;
using static ForgeMission.Tests.Cli.ForgeText;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (Phase 56 Task 4): the embedded Inter fonts. Both weights are embedded and load;
// the subset holds a glyph for every character image text allows, and the allowed-set rule matches
// the ruling exactly. A font that is missing or unreadable stops forge chat with a named error
// before the TUI starts (ForgeChat.LoadFonts).
public sealed class GlyphTextTests
{
    [Theory]
    [InlineData("Inter-SemiBold.ttf")]
    [InlineData("Inter-Bold.ttf")]
    public void Each_embedded_weight_has_a_glyph_for_every_allowed_character(string resource)
    {
        var glyphs = Glyphs(resource);

        Assert.All(AllowedSet, codepoint => Assert.True((bool)Call(glyphs, "HasGlyph", codepoint)!, $"no glyph for U+{codepoint:X4}"));
        Assert.False((bool)Call(glyphs, "HasGlyph", (int)'Ж')!);
    }

    [Fact]
    public void The_OFL_licence_is_embedded_with_the_fonts()
    {
        Assert.Contains("SIL OPEN FONT LICENSE", System.Text.Encoding.UTF8.GetString(Resource("Inter-LICENSE.txt")));
    }

    [Fact]
    public void Allows_takes_exactly_the_allowed_set()
    {
        var allowed = AllowedSet.ToHashSet();
        var outside = Enumerable.Range(0, 0x2200).Where(c => !allowed.Contains(c) && !char.IsSurrogate((char)c));

        Assert.All(AllowedSet, c => Assert.True(Allows(((char)c).ToString()), $"U+{c:X4} refused"));
        Assert.All(outside, c => Assert.False(Allows(((char)c).ToString()), $"U+{c:X4} allowed"));
        Assert.True(Allows("Step 1 — “Install” → done…"));
        Assert.False(Allows(""));
        Assert.False(Allows("Привет мир"));
        Assert.False(Allows("Hello 👋"));
    }

    [Fact]
    public void Kerning_tightens_a_kerned_pair()
    {
        var glyphs = Glyphs("Inter-SemiBold.ttf");
        double Advance(string text) => Get<double>(Call(glyphs, "Measure", text, 19.0, 0.0)!, "Advance");

        Assert.True(Advance("AV") < Advance("A") + Advance("V") - 1, "AV is not kerned");
        Assert.Equal(Advance("H") + Advance("H"), Advance("HH"), 6);
    }

    [Fact]
    public void A_missing_embedded_font_names_itself()
    {
        var thrown = Assert.ThrowsAny<Exception>(() => { Glyphs("Inter-Missing.ttf"); });

        var missing = thrown.InnerException!;
        Assert.IsType(Type("ForgeMission.Cli.Tui.Graphics.FontMissingException"), missing);
        Assert.Contains("Inter-Missing.ttf", missing.Message);
        Assert.Contains("Inter-SemiBold.ttf", missing.Message);
    }

    [Fact]
    public void LoadFonts_loads_both_embedded_weights()
    {
        var error = new StringWriter();

        Assert.NotNull(LoadFonts(error, null));
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unreadable")]
    public void LoadFonts_reports_a_font_it_cannot_load_and_gives_nothing(string failure)
    {
        Exception thrown = failure == "missing"
            ? (Exception)Activator.CreateInstance(Type("ForgeMission.Cli.Tui.Graphics.FontMissingException"), "Inter-Bold.ttf", new[] { "x" })!
            : new InvalidDataException("The font has no GPOS table.");
        var error = new StringWriter();

        Assert.Null(LoadFonts(error, Throwing(thrown)));
        Assert.StartsWith("forge chat: ", error.ToString());
        Assert.Contains(thrown.Message, error.ToString());
    }

    private static object? LoadFonts(TextWriter error, Delegate? load) =>
        Type("ForgeMission.Cli.ForgeChat").GetMethod("LoadFonts", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [error, load]);

    /// <summary>A Func&lt;TextFonts&gt; that throws <paramref name="thrown"/>.</summary>
    private static Delegate Throwing(Exception thrown)
    {
        var fonts = Type("ForgeMission.Cli.Tui.Graphics.TextFonts");
        return Expression.Lambda(typeof(Func<>).MakeGenericType(fonts), Expression.Throw(Expression.Constant(thrown), fonts)).Compile();
    }
}
