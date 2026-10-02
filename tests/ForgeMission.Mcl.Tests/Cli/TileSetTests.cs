using System.Collections.Concurrent;
using System.Reflection;
using XenoAtom.Terminal.UI;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (Phase 56 Tasks 2 and 3): every shape is a tile set — image tiles at its edges,
// plain text cells inside. Ring sets (card, code block, multi-line user message, composer) are
// eight tiles cut from one rendered template; cap sets (one-line user pill, APPROVED, tool chip)
// are a left and a right cap cut from a one-row pill. Product code only draws and cuts; the checks
// live here and run over the whole realistic cell-size range in both themes (every height 12–60 px
// at widths 0.40–0.60 × the height, how terminal cells are shaped):
//   seam     — each ring edge tile matches the template cells next to both of its corners;
//   edge     — a shadowed ring has faded into the surface at the template's outer pixels; every
//              other shape leaves the template's outer corner pixel on the surface;
//   interior — the interior's corner cells are exactly the fill token (they hold text on it);
//   join     — a cap's inner pixel column is exactly the pill fill token, so it meets its text
//              cells with no step.
// Image ids derive from (theme, set, cell size) and never repeat across inputs. Read from forge.dll
// through reflection like the other CLI tests.
[Collection(CpuBoundCollection.Name)]
public sealed class TileSetTests
{
    /// <summary>Largest max-channel difference (0..255 levels) treated as invisible.</summary>
    private const double CleanLevels = 1.0;

    /// <summary>A fill drawn in a tile must be its token exactly: the text cells beside it carry the
    /// token as their background, so any difference shows as a band.</summary>
    private const double ExactLevels = 0;

    private const int CapCols = 2;

    private static readonly string[] RingSets = ["CardShape", "CodeBlockShape", "UserShape", "ComposerShape"];
    private static readonly string[] CapSets = ["UserCaps", "ApprovedCaps", "ToolCaps"];

    private static readonly Assembly Forge = LoadForge();
    private static readonly Type ThemeType = Type("ForgeMission.Cli.Tui.ForgeTheme");
    private static readonly Type StylesType = Type("ForgeMission.Cli.Tui.ForgeStyles");
    private static readonly Type CellType = Type("ForgeMission.Cli.Tui.Graphics.CellSize");
    private static readonly MethodInfo Solve = Type("ForgeMission.Cli.Tui.Graphics.RingGeometry")
        .GetMethod("Solve", BindingFlags.Static | BindingFlags.Public)!;
    private static readonly MethodInfo RenderRing = Type("ForgeMission.Cli.Tui.Graphics.RingTiles")
        .GetMethod("Render", BindingFlags.Static | BindingFlags.Public)!;
    private static readonly MethodInfo RenderCaps = Type("ForgeMission.Cli.Tui.Graphics.CapTiles")
        .GetMethod("Render", BindingFlags.Static | BindingFlags.Public)!;
    private static readonly MethodInfo ImageIds = Type("ForgeMission.Cli.Tui.Graphics.TileSet")
        .GetMethod("ImageIds", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public void Ring_sets_are_clean_for_every_cell_size_in_both_themes()
    {
        var failures = new ConcurrentBag<string>();
        var sizes = from theme in new[] { "Light", "Dark" }
                    from set in RingSets
                    from height in Enumerable.Range(12, 49)
                    from width in Widths(height)
                    select (theme, set, width, height);

        Parallel.ForEach(sizes, size =>
        {
            var ring = Ring(size.theme, size.set, size.width, size.height);
            var (seam, edge, interior) = (SeamLevels(ring), EdgeLevels(ring), InteriorLevels(ring));
            if (seam > CleanLevels || edge > CleanLevels || interior > ExactLevels)
                failures.Add($"{size.theme} {size.set} {size.width}x{size.height}: seam={seam} edge={edge} interior={interior}");
        });

        Assert.Empty(failures.OrderBy(item => item));
    }

    [Fact]
    public void Cap_sets_join_their_text_cells_with_no_step_for_every_cell_size_in_both_themes()
    {
        var failures = new ConcurrentBag<string>();
        var sizes = from theme in new[] { "Light", "Dark" }
                    from set in CapSets
                    from height in Enumerable.Range(12, 49)
                    from width in Widths(height)
                    select (theme, set, width, height);

        Parallel.ForEach(sizes, size =>
        {
            var caps = Caps(size.theme, size.set, size.width, size.height);
            var (join, edge, interior, shape) = (JoinLevels(caps), CapEdgeLevels(caps), CapInteriorLevels(caps), CapShapeErrors(caps));
            if (join > ExactLevels || edge > CleanLevels || interior > ExactLevels || shape is not null)
                failures.Add($"{size.theme} {size.set} {size.width}x{size.height}: join={join} edge={edge} interior={interior} {shape}");
        });

        Assert.Empty(failures.OrderBy(item => item));
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void The_approved_cap_holds_the_success_dot(string theme)
    {
        foreach (var (width, height) in new[] { (19, 42), (10, 21) })
        {
            var caps = Caps(theme, "ApprovedCaps", width, height);
            var centre = height / 2;
            Assert.True(caps.Left.PixelError(centre, centre, caps.Dot!.Value) <= CleanLevels, $"{width}x{height}: no dot at the cap centre");
        }
    }

    [Theory]
    [InlineData("Light", 19, 42)]
    [InlineData("Dark", 19, 42)]
    [InlineData("Light", 10, 21)]
    [InlineData("Dark", 10, 21)]
    public void Ring_sizes_on_both_displays_are_the_designed_ones(string theme, int width, int height)
    {
        // (ring cols, top rows, bottom rows, text column from the ring's outer edge, text row)
        Assert.Equal((4, 2, 2), Size(Ring(theme, "CardShape", width, height)));
        Assert.Equal(1, Ring(theme, "CardShape", width, height).SideInset / width);
        foreach (var set in new[] { "CodeBlockShape", "UserShape", "ComposerShape" })
        {
            var ring = Ring(theme, set, width, height);
            Assert.Equal((2, 1, 1), Size(ring));
            Assert.Equal((0, 0, 0), (ring.PadCols, ring.PadTop, ring.PadBottom));
        }
    }

    [Theory]
    [InlineData("Light", 19, 42)]
    [InlineData("Dark", 10, 21)]
    public void Each_tile_image_covers_exactly_its_cells(string theme, int width, int height)
    {
        foreach (var set in RingSets)
        foreach (var tile in Ring(theme, set, width, height).Tiles)
            Assert.Equal((tile.Cols * width, tile.Rows * height), (tile.Image.Width, tile.Image.Height));
        foreach (var set in CapSets)
        {
            var caps = Caps(theme, set, width, height);
            Assert.Equal((CapCols * width, height), (caps.Left.Width, caps.Left.Height));
            Assert.Equal((CapCols * width, height), (caps.Right.Width, caps.Right.Height));
        }
    }

    [Fact]
    public void Image_ids_are_disjoint_across_themes_sets_and_cell_sizes_and_fit_24_bits()
    {
        var seen = new HashSet<uint>();
        var inputs = from theme in new[] { 0, 1 }
                     from set in Enumerable.Range(0, 7)
                     from height in Enumerable.Range(12, 49)
                     from width in Enumerable.Range(4, 40)
                     select (theme, set, width, height);
        foreach (var (theme, set, width, height) in inputs)
        {
            var ids = Ids(theme, set, width, height);
            Assert.Equal(8, ids.Length);
            Assert.All(ids, id => Assert.InRange(id, 1u, 0xFFFFFFu));
            Assert.All(ids, id => Assert.True(seen.Add(id), $"id {id} repeats at theme {theme} set {set} {width}x{height}"));
        }
    }

    [Fact]
    public void Image_ids_are_stable()
    {
        Assert.Equal(Ids(1, 3, 19, 42), Ids(1, 3, 19, 42));
        // theme 0 | width 19 | height 42 | set 2 | slot: ((((0 << 7 | 19) << 8 | 42) << 3 | 2) << 3) + slot
        Assert.Equal(Enumerable.Range(0, 8).Select(slot => (uint)(((((19 << 8) | 42) << 3 | 2) << 3) + slot)), Ids(0, 2, 19, 42));
    }

    [Fact]
    public void Image_ids_reject_inputs_outside_their_bits()
    {
        Assert.IsType<ArgumentOutOfRangeException>(Assert.Throws<TargetInvocationException>(() => Ids(0, 0, 128, 42)).InnerException);
        Assert.IsType<ArgumentOutOfRangeException>(Assert.Throws<TargetInvocationException>(() => Ids(0, 0, 19, 256)).InnerException);
        Assert.IsType<ArgumentOutOfRangeException>(Assert.Throws<TargetInvocationException>(() => Ids(0, 8, 19, 42)).InnerException);
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

    private static (int, int, int) Size(RingFacts r) => (r.SideCols, r.TopRows, r.BottomRows);

    // ── Ring checks (max channel difference, 0..255 levels) ─────────────────────────────────

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

    /// <summary>A shadowed ring: its outermost pixels against the plain surface. Any other ring:
    /// its four outer corner pixels (outside the rounded corner).</summary>
    private static double EdgeLevels(RingFacts r)
    {
        var t = r.Template;
        IEnumerable<(int, int)> pixels = r.Shadowed
            ? Enumerable.Range(0, t.Width).SelectMany(x => new[] { (x, 0), (x, t.Height - 1) })
                .Concat(Enumerable.Range(0, t.Height).SelectMany(y => new[] { (0, y), (t.Width - 1, y) }))
            : [(0, 0), (t.Width - 1, 0), (0, t.Height - 1), (t.Width - 1, t.Height - 1)];
        return pixels.Max(p => t.PixelError(p.Item1, p.Item2, r.Surface));
    }

    /// <summary>The interior's four corner cells against the shape's plain fill.</summary>
    private static double InteriorLevels(RingFacts r)
    {
        int lastCol = r.SideCols + r.InteriorCols - 1, lastRow = r.TopRows + r.InteriorRows - 1;
        (int Col, int Row)[] corners = [(r.SideCols, r.TopRows), (lastCol, r.TopRows), (r.SideCols, lastRow), (lastCol, lastRow)];
        return corners.Max(cell => CellError(r.Template, r.CellWidth, r.CellHeight, cell.Col, cell.Row, r.Fill));
    }

    private static double CellError(Image image, int cellWidth, int cellHeight, int col, int row, (byte R, byte G, byte B) color)
    {
        var pixels = from y in Enumerable.Range(row * cellHeight, cellHeight)
                     from x in Enumerable.Range(col * cellWidth, cellWidth)
                     select image.PixelError(x, y, color);
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

    // ── Cap checks ──────────────────────────────────────────────────────────────────────────

    /// <summary>Each cap's inner pixel column, every row, against the pill fill.</summary>
    private static double JoinLevels(CapFacts c)
    {
        var rows = Enumerable.Range(0, c.Left.Height);
        return rows.Max(y => Math.Max(c.Left.PixelError(c.Left.Width - 1, y, c.Fill), c.Right.PixelError(0, y, c.Fill)));
    }

    /// <summary>The outer corner pixels of both caps (outside the half-round end) against the surface.</summary>
    private static double CapEdgeLevels(CapFacts c)
    {
        int w = c.Left.Width, h = c.Left.Height;
        return new[]
        {
            c.Left.PixelError(0, 0, c.Surface), c.Left.PixelError(0, h - 1, c.Surface),
            c.Right.PixelError(w - 1, 0, c.Surface), c.Right.PixelError(w - 1, h - 1, c.Surface),
        }.Max();
    }

    /// <summary>The template's cells between the caps against the pill fill.</summary>
    private static double CapInteriorLevels(CapFacts c)
    {
        var cols = c.Template.Width / c.CellWidth;
        return Enumerable.Range(CapCols, cols - 2 * CapCols).Max(col => CellError(c.Template, c.CellWidth, c.CellHeight, col, 0, c.Fill));
    }

    /// <summary>The caps are the template's first and last two columns, one row tall.</summary>
    private static string? CapShapeErrors(CapFacts c)
    {
        if (c.Template.Height != c.CellHeight) return "template is not one row";
        if (c.Left.Width != CapCols * c.CellWidth || c.Right.Width != CapCols * c.CellWidth) return "caps are not two columns";
        return null;
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
        int PadCols, int PadTop, int PadBottom, bool Shadowed,
        Image Template, IReadOnlyList<TileFacts> Tiles, (byte R, byte G, byte B) Surface, (byte R, byte G, byte B) Fill)
    {
        public int InteriorCols => Template.Width / CellWidth - 2 * SideCols;

        public int InteriorRows => Template.Height / CellHeight - TopRows - BottomRows;
    }

    private sealed record CapFacts(int CellWidth, int CellHeight, Image Left, Image Right, Image Template,
        (byte R, byte G, byte B) Surface, (byte R, byte G, byte B) Fill, (byte R, byte G, byte B)? Dot);

    private static RingFacts Ring(string theme, string set, int width, int height)
    {
        var shape = Shape(theme, set);
        var layout = Solve.Invoke(null, [shape, Cell(width, height)])!;
        var tiles = RenderRing.Invoke(null, [shape, layout])!;
        var all = ((System.Collections.IEnumerable)Get(tiles, "All")).Cast<object>()
            .Select(tile => new TileFacts(ImageOf(Get(tile, "Image")), (int)Get(tile, "Cols"), (int)Get(tile, "Rows")))
            .ToList();
        var shadowed = ((System.Collections.ICollection)Get(shape, "Shadows")).Count > 0;
        return new RingFacts(width, height, Int(layout, "SideCols"), Int(layout, "TopRows"), Int(layout, "BottomRows"),
            Int(layout, "SideInset"), Int(layout, "PadCols"), Int(layout, "PadTop"), Int(layout, "PadBottom"), shadowed,
            ImageOf(Get(tiles, "Template")), all, Rgb(Get(shape, "Surface")), Rgb(Get(shape, "Fill")));
    }

    private static CapFacts Caps(string theme, string set, int width, int height)
    {
        var shape = Shape(theme, set);
        var caps = RenderCaps.Invoke(null, [shape, Cell(width, height)])!;
        var dot = shape.GetType().GetProperty("Dot")!.GetValue(shape) is Color c ? Rgb(c) : ((byte, byte, byte)?)null;
        return new CapFacts(width, height, ImageOf(Get(Get(caps, "Left"), "Image")), ImageOf(Get(Get(caps, "Right"), "Image")),
            ImageOf(Get(caps, "Template")), Rgb(Get(shape, "Surface")), Rgb(Get(shape, "Fill")), dot);
    }

    private static object Shape(string theme, string set) =>
        StylesType.GetProperty(set)!.GetValue(Activator.CreateInstance(StylesType, Theme(theme)))!;

    private static object Cell(int width, int height) => Activator.CreateInstance(CellType, width, height)!;

    private static Image ImageOf(object image) => new((int)Get(image, "Width"), (int)Get(image, "Height"), (byte[])Get(image, "Pixels"));

    private static (byte R, byte G, byte B) Rgb(object color) => (((Color)color).R, ((Color)color).G, ((Color)color).B);

    private static int Int(object target, string name) => (int)Get(target, name);

    private static uint[] Ids(int theme, int set, int width, int height) => (uint[])ImageIds.Invoke(null, [theme, set, width, height])!;

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
