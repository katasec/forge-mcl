using System.Collections.Concurrent;
using System.Collections;
using System.Reflection;
using static ForgeMission.Tests.Cli.ForgeText;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (Phase 56 Task 4): the text images, drawn for every cell height 12–60 px (width
// half the height) in both themes. Each image meets the cells beside it with no band: its left and
// right pixel columns are exactly the surface token the neighbouring cells carry. Text kinds (name,
// breadcrumb, heading line) keep their ink inside the image — the top and bottom rows are surface
// too, even with an accented capital and descenders — from a 20 px cell height up (the 1× display
// is 21 px; below 20 px an accent on a capital can reach the row's first pixel). Sizes follow the
// mockup at the two displays, and wrapping keeps every line inside its width.
[Collection(CpuBoundCollection.Name)]
public sealed class TextArtTests
{
    private static readonly (string Kind, string Text, string Surface)[] Images =
    [
        ("Brand", "forge", "SurfaceHeader"),
        ("Crumb", "Ágjy / Chat", "SurfaceHeader"),
        ("Name", "Ágjy Answerer", "CardSurface"),
        ("Heading1", "Ágjy Hello World", "CardSurface"),
        ("Heading2", "Ágjy Hello World", "CardSurface"),
        ("Heading3", "Ágjy Hello World", "CardSurface"),
        ("Avatar", "A", "CardSurface"),
        ("UserAvatar", "W", "Surface"),
        ("Chip", "pgup/pgdn", "Surface"),
        ("Send", "↵", "CardSurface"),
    ];

    private static readonly string[] TextKinds = ["Crumb", "Name", "Heading1", "Heading2", "Heading3"];
    private const int InkInsideFromHeight = 20;

    [Fact]
    public void Every_image_meets_its_neighbours_with_no_band_and_keeps_its_ink_inside()
    {
        var failures = new ConcurrentBag<string>();
        var cases = from theme in new[] { "Light", "Dark" } from height in Enumerable.Range(12, 49) select (theme, height);

        Parallel.ForEach(cases, c =>
        {
            var art = Art(c.theme, Math.Max(1, c.height / 2), c.height);
            foreach (var (kind, text, surface) in Images)
            {
                var image = Get<object>(Call(art, "Render", Request(kind, text, kind == "Crumb" ? 7 : 0))!, "Image");
                var expected = Token(c.theme, surface);
                var edges = Edges(image, top: TextKinds.Contains(kind) && c.height >= InkInsideFromHeight);
                if (edges.Any(p => p != expected))
                    failures.Add($"{c.theme} h={c.height} {kind}: edge pixel {edges.First(p => p != expected)} is not {surface} {expected}");
            }
        });

        Assert.True(failures.IsEmpty, string.Join('\n', failures.Take(20)));
    }

    [Theory]
    [InlineData(10, 21, "Name", "Answerer", 8, 1)]
    [InlineData(19, 42, "Name", "Answerer", 8, 1)]
    [InlineData(10, 21, "Heading2", "Hello World in Pascal", 20, 2)]
    [InlineData(19, 42, "Heading2", "Hello World in Pascal", 21, 2)]
    [InlineData(10, 21, "Brand", "forge", 8, 1)]
    [InlineData(19, 42, "Brand", "forge", 8, 1)]
    [InlineData(10, 21, "Avatar", "A", 3, 1)]
    [InlineData(19, 42, "Avatar", "A", 3, 1)]
    [InlineData(10, 21, "Chip", "pgup/pgdn", 8, 1)]
    [InlineData(19, 42, "Chip", "pgup/pgdn", 9, 1)]
    [InlineData(10, 21, "Send", "↵", 3, 1)]
    [InlineData(19, 42, "Send", "↵", 3, 1)]
    public void Images_take_the_cells_the_mockup_gives_them_on_both_displays(int width, int height, string kind, string text, int cols, int rows)
    {
        var image = Call(Art("Light", width, height), "Render", Request(kind, text))!;

        Assert.Equal((cols, rows), (Get<int>(image, "Cols"), Get<int>(image, "Rows")));
        var pixels = Get<object>(image, "Image");
        Assert.Equal((cols * width, rows * height), (Get<int>(pixels, "Width"), Get<int>(pixels, "Height")));
    }

    [Fact]
    public void A_crumb_draws_its_prefix_muted_and_the_mission_strong()
    {
        var image = Get<object>(Call(Art("Light", 19, 42), "Render", Request("Crumb", "chat / Chat", 7))!, "Image");
        var pixels = Pixels(image);

        Assert.Contains(pixels, p => Near(p, Token("Light", "TextMuted")));
        Assert.Contains(pixels, p => Near(p, Token("Light", "TextStrong")));
    }

    [Fact]
    public void A_heading_wraps_at_words_and_cuts_an_overlong_word_with_an_ellipsis()
    {
        var art = Art("Light", 10, 21);
        const string wrapped = "Hello   World in Pascal and a bit more";
        const string longWord = "Supercalifragilisticexpialidocious";
        var lines = HeadingLines(art, wrapped, 12);
        var single = HeadingLines(art, longWord, 12);

        Assert.True(lines.Count > 1);
        Assert.Equal("Hello World in Pascal and a bit more", string.Join(' ', lines.Select(Text)));
        Assert.All(lines, line => Assert.True(Cols(art, Text(line)) <= 12, $"'{Text(line)}' is wider than 12 columns"));
        Assert.Equal(0, Start(lines[0]));
        Assert.Equal(wrapped.Length, End(lines[^1]));
        Assert.All(lines, line => Assert.Equal(Normalize(wrapped[Start(line)..End(line)]), Text(line)));
        Assert.All(lines, line => Assert.Equal(Start(line), Boundary(line, "SourceIndexAtVisual", 0)));
        Assert.True(lines.Zip(lines.Skip(1)).All(pair => End(pair.First) <= Start(pair.Second)));
        var cut = Assert.Single(single);
        Assert.EndsWith("…", Text(cut));
        Assert.Equal((0, longWord.Length), (Start(cut), End(cut)));
        Assert.True(Cols(art, Text(cut)) <= 12);
        Assert.Equal(longWord.Length, Boundary(cut, "SourceIndexAtVisual", Text(cut).Length));
        Assert.Equal(Text(cut).Length, Boundary(cut, "VisualIndexAtSource", longWord.Length));
    }

    private static int Cols(object art, string line) => Get<int>(Call(art, "Render", Request("Heading2", line))!, "Cols");

    private static List<object> HeadingLines(object art, string text, int cols) =>
        ((IEnumerable)Call(art, "Wrap", Kind("Heading2"), text, cols)!).Cast<object>().ToList();

    private static string Text(object line) => Value<string>(line, "Text");

    private static int Start(object line) => Value<int>(line, "SourceStart");

    private static int End(object line) => Value<int>(line, "SourceEnd");

    private static T Value<T>(object line, string property) => (T)line.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(line)!;

    private static int Boundary(object line, string method, int index) => (int)line.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(line, [index])!;

    private static string Normalize(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The left and right pixel columns, and the top and bottom rows when asked.</summary>
    private static List<(byte, byte, byte)> Edges(object image, bool top)
    {
        var (w, h, pixels) = (Get<int>(image, "Width"), Get<int>(image, "Height"), Get<byte[]>(image, "Pixels"));
        (byte, byte, byte) At(int x, int y) => (pixels[(y * w + x) * 3], pixels[(y * w + x) * 3 + 1], pixels[(y * w + x) * 3 + 2]);
        var edges = new List<(byte, byte, byte)>();
        for (var y = 0; y < h; y++) edges.AddRange([At(0, y), At(w - 1, y)]);
        if (top) for (var x = 0; x < w; x++) edges.AddRange([At(x, 0), At(x, h - 1)]);
        return edges;
    }

    private static List<(byte, byte, byte)> Pixels(object image)
    {
        var pixels = Get<byte[]>(image, "Pixels");
        return [.. Enumerable.Range(0, pixels.Length / 3).Select(i => (pixels[i * 3], pixels[i * 3 + 1], pixels[i * 3 + 2]))];
    }

    private static bool Near((byte R, byte G, byte B) p, (byte R, byte G, byte B) c) =>
        Math.Abs(p.R - c.R) <= 2 && Math.Abs(p.G - c.G) <= 2 && Math.Abs(p.B - c.B) <= 2;

    private static (byte, byte, byte) Token(string theme, string name)
    {
        var color = (XenoAtom.Terminal.UI.Color)Type("ForgeMission.Cli.Tui.ForgeTheme").GetProperty(name)!.GetValue(Theme(theme))!;
        return (color.R, color.G, color.B);
    }
}
