using System.Collections.Concurrent;
using System.Reflection;
using XenoAtom.Terminal.UI;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (Phase 56 Task 2): participant cards are framed by eight edge tiles cut from one
// rendered template card. Product code only draws and cuts; the checks live here and run over
// the whole realistic cell-size range in both themes (every height 12–60 px at widths 0.40–0.60
// × the height, how terminal cells are shaped):
//   seam     — each edge tile matches the template cells next to both of its corners;
//   edge     — the shadow has faded into the surface at the template's outer pixels;
//   interior — the interior's corner cells are plain card surface (they hold text).
// Image ids derive from (theme, cell size) and never repeat across inputs. Read from forge.dll
// through reflection like the other CLI tests.
public sealed class CardEdgeTests
{
    /// <summary>Largest max-channel difference (0..255 levels) treated as invisible.</summary>
    private const double CleanLevels = 1.0;

    private static readonly Assembly Forge = LoadForge();
    private static readonly Type ThemeType = Type("ForgeMission.Cli.Tui.ForgeTheme");
    private static readonly Type StylesType = Type("ForgeMission.Cli.Tui.ForgeStyles");
    private static readonly Type CellType = Type("ForgeMission.Cli.Tui.Graphics.CellSize");
    private static readonly MethodInfo Solve = Type("ForgeMission.Cli.Tui.Graphics.RingGeometry")
        .GetMethod("Solve", BindingFlags.Static | BindingFlags.Public)!;
    private static readonly MethodInfo Render = Type("ForgeMission.Cli.Tui.Graphics.CardTiles")
        .GetMethod("Render", BindingFlags.Static | BindingFlags.Public)!;
    private static readonly MethodInfo ImageIds = Type("ForgeMission.Cli.Tui.Graphics.CardRing")
        .GetMethod("ImageIds", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public void Tiles_are_clean_for_every_cell_size_in_both_themes()
    {
        var failures = new ConcurrentBag<string>();
        var sizes = from theme in new[] { "Light", "Dark" }
                    from height in Enumerable.Range(12, 49)
                    from width in Widths(height)
                    select (theme, width, height);

        Parallel.ForEach(sizes, size =>
        {
            var ring = Ring(size.theme, size.width, size.height);
            var (seam, edge, interior) = (SeamLevels(ring), EdgeLevels(ring), InteriorLevels(ring));
            if (seam > CleanLevels || edge > CleanLevels || interior > CleanLevels)
                failures.Add($"{size.theme} {size.width}x{size.height}: seam={seam} edge={edge} interior={interior}");
        });

        Assert.Empty(failures.OrderBy(item => item));
    }

    [Theory]
    [InlineData("Light", 19, 42)]
    [InlineData("Dark", 19, 42)]
    [InlineData("Light", 10, 21)]
    [InlineData("Dark", 10, 21)]
    public void The_ring_is_four_columns_wide_and_two_rows_tall_on_both_displays(string theme, int width, int height)
    {
        var ring = Ring(theme, width, height);

        Assert.Equal((4, 2, 2), (ring.SideCols, ring.TopRows, ring.BottomRows));
        Assert.Equal(1, ring.SideInset / width);
    }

    [Theory]
    [InlineData("Light", 19, 42)]
    [InlineData("Dark", 10, 21)]
    public void Each_tile_image_covers_exactly_its_cells(string theme, int width, int height)
    {
        foreach (var tile in Ring(theme, width, height).Tiles)
            Assert.Equal((tile.Cols * width, tile.Rows * height), (tile.Image.Width, tile.Image.Height));
    }

    [Fact]
    public void Image_ids_are_disjoint_across_themes_and_cell_sizes_and_fit_24_bits()
    {
        var seen = new HashSet<uint>();
        var inputs = from theme in new[] { 0, 1 }
                     from height in Enumerable.Range(12, 49)
                     from width in Enumerable.Range(4, 40)
                     select (theme, width, height);
        foreach (var (theme, width, height) in inputs)
        {
            var ids = Ids(theme, width, height);
            Assert.Equal(8, ids.Length);
            Assert.All(ids, id => Assert.InRange(id, 1u, 0xFFFFFFu));
            Assert.All(ids, id => Assert.True(seen.Add(id), $"id {id} repeats at theme {theme} {width}x{height}"));
        }
    }

    [Fact]
    public void Image_ids_are_stable()
    {
        Assert.Equal(Ids(1, 19, 42), Ids(1, 19, 42));
        // theme 0 | width 19 | height 42 | slot: (((0 << 9 | 19) << 11 | 42) << 3) + slot
        Assert.Equal(Enumerable.Range(0, 8).Select(slot => (uint)((((19 << 11) | 42) << 3) + slot)), Ids(0, 19, 42));
    }

    [Fact]
    public void The_themes_take_different_image_id_slots()
    {
        Assert.Equal(0, ImageIdSlot("Light"));
        Assert.Equal(1, ImageIdSlot("Dark"));
    }

    /// <summary>Cell widths from round(0.40·h) to round(0.60·h).</summary>
    private static IEnumerable<int> Widths(int height)
    {
        var first = (int)Math.Round(0.40 * height, MidpointRounding.AwayFromZero);
        var last = (int)Math.Round(0.60 * height, MidpointRounding.AwayFromZero);
        return Enumerable.Range(first, last - first + 1);
    }

    // ── Checks (max channel difference, 0..255 levels) ──────────────────────────────────────

    /// <summary>Each edge tile against the template cells right next to both of its corners.</summary>
    private static double SeamLevels(RingFacts r)
    {
        int firstCol = r.SideCols, lastCol = r.SideCols + r.InteriorCols - 1, rightCol = r.SideCols + r.InteriorCols;
        int firstRow = r.TopRows, lastRow = r.TopRows + r.InteriorRows - 1, bottomRow = r.TopRows + r.InteriorRows;
        var (top, left, right, bottom) = (r.Tiles[1].Image, r.Tiles[3].Image, r.Tiles[4].Image, r.Tiles[6].Image);
        return new[]
        {
            Differs(r, top, firstCol, 0), Differs(r, top, lastCol, 0),
            Differs(r, bottom, firstCol, bottomRow), Differs(r, bottom, lastCol, bottomRow),
            Differs(r, left, 0, firstRow), Differs(r, left, 0, lastRow),
            Differs(r, right, rightCol, firstRow), Differs(r, right, rightCol, lastRow),
        }.Max();
    }

    /// <summary>The template's outermost pixels against the plain surface.</summary>
    private static double EdgeLevels(RingFacts r)
    {
        var t = r.Template;
        var border = Enumerable.Range(0, t.Width).SelectMany(x => new[] { (x, 0), (x, t.Height - 1) })
            .Concat(Enumerable.Range(0, t.Height).SelectMany(y => new[] { (0, y), (t.Width - 1, y) }));
        return border.Max(p => t.PixelError(p.Item1, p.Item2, r.Surface));
    }

    /// <summary>The interior's four corner cells against plain card surface.</summary>
    private static double InteriorLevels(RingFacts r)
    {
        int lastCol = r.SideCols + r.InteriorCols - 1, lastRow = r.TopRows + r.InteriorRows - 1;
        (int Col, int Row)[] corners = [(r.SideCols, r.TopRows), (lastCol, r.TopRows), (r.SideCols, lastRow), (lastCol, lastRow)];
        return corners.Max(cell => CellError(r, cell.Col, cell.Row));
    }

    private static double CellError(RingFacts r, int col, int row)
    {
        var pixels = from y in Enumerable.Range(row * r.CellHeight, r.CellHeight)
                     from x in Enumerable.Range(col * r.CellWidth, r.CellWidth)
                     select r.Template.PixelError(x, y, r.CardSurface);
        return pixels.Max();
    }

    /// <summary>A tile against the same-sized template region at cell (col, row).</summary>
    private static double Differs(RingFacts r, Image tile, int col, int row)
    {
        int x0 = col * r.CellWidth, y0 = row * r.CellHeight, max = 0;
        for (var y = 0; y < tile.Height; y++)
            max = Math.Max(max, RowDifference(tile, r.Template, y, x0, y0 + y));
        return max;
    }

    private static int RowDifference(Image tile, Image template, int tileRow, int x0, int templateRow)
    {
        var max = 0;
        for (var i = 0; i < tile.Width * 3; i++)
            max = Math.Max(max, Math.Abs(tile.Pixels[tileRow * tile.Width * 3 + i] - template.Pixels[(templateRow * template.Width + x0) * 3 + i]));
        return max;
    }

    // ── Reflection ──────────────────────────────────────────────────────────────────────────

    private sealed record Image(int Width, int Height, byte[] Pixels)
    {
        public int PixelError(int x, int y, (byte R, byte G, byte B) c)
        {
            var i = (y * Width + x) * 3;
            return Math.Max(Math.Abs(Pixels[i] - c.R), Math.Max(Math.Abs(Pixels[i + 1] - c.G), Math.Abs(Pixels[i + 2] - c.B)));
        }
    }

    private sealed record TileFacts(Image Image, int Cols, int Rows);

    private sealed record RingFacts(int CellWidth, int CellHeight, int SideCols, int TopRows, int BottomRows, int SideInset,
        Image Template, IReadOnlyList<TileFacts> Tiles, (byte R, byte G, byte B) Surface, (byte R, byte G, byte B) CardSurface)
    {
        public int InteriorCols => Template.Width / CellWidth - 2 * SideCols;

        public int InteriorRows => Template.Height / CellHeight - TopRows - BottomRows;
    }

    private static RingFacts Ring(string theme, int width, int height)
    {
        var tokens = Theme(theme);
        var edges = StylesType.GetProperty("CardEdges")!.GetValue(Activator.CreateInstance(StylesType, tokens))!;
        var layout = Solve.Invoke(null, [edges, Activator.CreateInstance(CellType, width, height)])!;
        var tiles = Render.Invoke(null, [edges, layout])!;
        var all = ((System.Collections.IEnumerable)Get(tiles, "All")).Cast<object>()
            .Select(tile => new TileFacts(ImageOf(Get(tile, "Image")), (int)Get(tile, "Cols"), (int)Get(tile, "Rows")))
            .ToList();
        return new RingFacts(width, height, (int)Get(layout, "SideCols"), (int)Get(layout, "TopRows"), (int)Get(layout, "BottomRows"),
            (int)Get(layout, "SideInset"), ImageOf(Get(tiles, "Template")), all, Token(tokens, "Surface"), Token(tokens, "CardSurface"));
    }

    private static Image ImageOf(object image) => new((int)Get(image, "Width"), (int)Get(image, "Height"), (byte[])Get(image, "Pixels"));

    private static (byte R, byte G, byte B) Token(object tokens, string name)
    {
        var color = (Color)ThemeType.GetProperty(name)!.GetValue(tokens)!;
        return (color.R, color.G, color.B);
    }

    private static uint[] Ids(int theme, int width, int height) => (uint[])ImageIds.Invoke(null, [theme, width, height])!;

    private static int ImageIdSlot(string theme) =>
        (int)StylesType.GetProperty("ImageIdSlot")!.GetValue(Activator.CreateInstance(StylesType, Theme(theme)))!;

    private static object Theme(string name) =>
        ThemeType.GetProperty(name, BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;

    private static object Get(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;

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
