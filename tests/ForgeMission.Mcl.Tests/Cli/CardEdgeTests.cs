using System.Collections.Concurrent;
using System.Reflection;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (Phase 56 Task 2): participant cards are framed by eight edge tiles cut from one
// rendered template card. The spike's checks run here over the whole realistic cell-size range in
// both themes: each edge tile matches the cells next to the corners (no seam), the shadow has faded
// into the surface at the ring's outer edge, and the interior cells are plain card surface (they
// hold text). Image ids derive from (theme, cell size) and never repeat across inputs. Read from
// forge.dll through reflection like the other CLI tests.
public sealed class CardEdgeTests
{
    private const double CleanLevels = 1.0;

    private static readonly Assembly Forge = LoadForge();
    private static readonly Type ThemeType = Type("ForgeMission.Cli.Tui.ForgeTheme");
    private static readonly Type StylesType = Type("ForgeMission.Cli.Tui.ForgeStyles");
    private static readonly Type CellType = Type("ForgeMission.Cli.Tui.Graphics.CellSize");
    private static readonly MethodInfo Solve = Type("ForgeMission.Cli.Tui.Graphics.RingGeometry")
        .GetMethod("Solve", BindingFlags.Static | BindingFlags.Public)!;
    private static readonly MethodInfo ImageIds = Type("ForgeMission.Cli.Tui.Graphics.CardRing")
        .GetMethod("ImageIds", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public void Tiles_are_clean_for_every_cell_size_in_both_themes()
    {
        var failures = new ConcurrentBag<string>();
        var sizes = from theme in new[] { "Light", "Dark" }
                    from width in Enumerable.Range(6, 25)
                    from height in Enumerable.Range(12, 49)
                    select (theme, width, height);

        Parallel.ForEach(sizes, size =>
        {
            var ring = Ring(size.theme, size.width, size.height);
            if (ring.Seam > CleanLevels || ring.Edge > CleanLevels || ring.Interior > CleanLevels)
                failures.Add($"{size.theme} {size.width}x{size.height}: seam={ring.Seam} edge={ring.Edge} interior={ring.Interior}");
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
        Assert.Equal(1, ring.BorderCol);
    }

    [Theory]
    [InlineData("Light", 19, 42)]
    [InlineData("Dark", 10, 21)]
    public void Each_tile_image_covers_exactly_its_cells(string theme, int width, int height)
    {
        foreach (var (pixelWidth, pixelHeight, cols, rows) in Ring(theme, width, height).Tiles)
            Assert.Equal((cols * width, rows * height), (pixelWidth, pixelHeight));
    }

    [Fact]
    public void Image_ids_are_disjoint_across_themes_and_cell_sizes_and_fit_24_bits()
    {
        var seen = new HashSet<uint>();
        foreach (var theme in new[] { 0, 1 })
        foreach (var width in Enumerable.Range(6, 25))
        foreach (var height in Enumerable.Range(12, 49))
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
        Assert.Equal(0, ImageIdTheme("Light"));
        Assert.Equal(1, ImageIdTheme("Dark"));
    }

    // ── Reflection ──────────────────────────────────────────────────────────────────────────

    private sealed record RingFacts(int SideCols, int TopRows, int BottomRows, int BorderCol, double Seam, double Edge, double Interior,
        IReadOnlyList<(int Width, int Height, int Cols, int Rows)> Tiles);

    private static RingFacts Ring(string theme, int width, int height)
    {
        var styles = Activator.CreateInstance(StylesType, Theme(theme))!;
        var edges = StylesType.GetProperty("CardEdges")!.GetValue(styles)!;
        var solved = Solve.Invoke(null, [edges, Activator.CreateInstance(CellType, width, height)])!;
        var layout = solved.GetType().GetField("Item1")!.GetValue(solved)!;
        var tiles = solved.GetType().GetField("Item2")!.GetValue(solved)!;
        var all = ((System.Collections.IEnumerable)Get(tiles, "All")).Cast<object>()
            .Select(tile =>
            {
                var image = Get(tile, "Image");
                return ((int)Get(image, "Width"), (int)Get(image, "Height"), (int)Get(tile, "Cols"), (int)Get(tile, "Rows"));
            })
            .ToList();
        return new RingFacts((int)Get(layout, "SideCols"), (int)Get(layout, "TopRows"), (int)Get(layout, "BottomRows"),
            (int)Get(layout, "SideInset") / width,
            (double)Get(tiles, "SeamLevels"), (double)Get(tiles, "EdgeLevels"), (double)Get(tiles, "InteriorLevels"), all);
    }

    private static uint[] Ids(int theme, int width, int height) => (uint[])ImageIds.Invoke(null, [theme, width, height])!;

    private static int ImageIdTheme(string theme) =>
        (int)StylesType.GetProperty("ImageIdTheme")!.GetValue(Activator.CreateInstance(StylesType, Theme(theme)))!;

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
