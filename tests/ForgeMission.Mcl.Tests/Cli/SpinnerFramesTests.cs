using XenoAtom.Terminal.UI;
using static ForgeMission.Tests.Cli.ForgeText;

namespace ForgeMission.Tests.Cli;

// Phase 56 Task 5: the spinners' image frames (progress row and tool chip). Ten frames per spinner,
// each two cells wide and one tall at every realistic cell size, the ring whole inside them on its
// surface, each frame the arc turned 36° further; ids with bit 22 set that never meet a tile or
// text image id.
public sealed class SpinnerFramesTests
{
    private static readonly Type FramesType = Type("ForgeMission.Cli.Tui.Graphics.SpinnerFrames");
    private static readonly string[] Spinners = ["ProgressSpinner", "ToolSpinner"];

    public static TheoryData<string, string> Every => new()
    {
        { "Light", "ProgressSpinner" }, { "Light", "ToolSpinner" }, { "Dark", "ProgressSpinner" }, { "Dark", "ToolSpinner" },
    };

    [Theory]
    [MemberData(nameof(Every))]
    public void Ten_frames_two_cells_wide_with_the_ring_whole_on_its_surface_at_every_cell_size(string theme, string spinner)
    {
        var shape = Get<object>(Styles(theme), spinner);
        var surface = Get<Color>(shape, "Surface");
        for (var height = 12; height <= 60; height++)
        foreach (var width in new[] { (int)Math.Round(height * 0.40), (int)Math.Round(height * 0.50), (int)Math.Round(height * 0.60) })
        {
            var frames = Frames(theme, spinner, width, height);
            Assert.Equal(10, frames.Count);
            foreach (var (pixels, w, h, cols, rows) in frames)
            {
                Assert.Equal((2, 1, 2 * width, height), (cols, rows, w, h));
                // The outermost pixel columns and rows are plain surface: the ring is never cut.
                for (var y = 0; y < h; y++)
                {
                    Assert.True(Error(pixels, w, 0, y, surface) <= 1, $"{theme} {spinner} {width}x{height}: left edge");
                    Assert.True(Error(pixels, w, w - 1, y, surface) <= 1, $"{theme} {spinner} {width}x{height}: right edge");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Every))]
    public void Each_frame_turns_the_arc_36_degrees_clockwise(string theme, string spinner)
    {
        var shape = Get<object>(Styles(theme), spinner);
        var arc = Get<Color>(shape, "Arc");
        var frames = Frames(theme, spinner, 19, 42);
        for (var i = 0; i < frames.Count; i++)
        {
            var (pixels, w, h, _, _) = frames[i];
            var angle = ArcAngle(pixels, w, h, arc);
            var expected = 36.0 * i;
            var off = Math.Abs(((angle - expected) % 360 + 540) % 360 - 180);
            Assert.True(off <= 6, $"frame {i}: arc at {angle:F1}°, expected {expected}°");
        }
    }

    [Fact]
    public void Spinner_ids_set_bit_22_and_never_meet_tile_or_text_ids()
    {
        var tileIds = new HashSet<uint>();
        var spinnerIds = new HashSet<uint>();
        foreach (var (w, h) in new[] { (19, 42), (10, 21), (12, 30) })
        for (var theme = 0; theme <= 1; theme++)
        {
            for (var set = 0; set < 8; set++)
                tileIds.UnionWith((uint[])Type("ForgeMission.Cli.Tui.Graphics.TileSet").GetMethod("ImageIds", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [theme, set, w, h])!);
            for (var spinner = 0; spinner <= 1; spinner++)
            {
                var ids = (uint[])FramesType.GetMethod("ImageIds", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [theme, spinner, w, h])!;
                Assert.Equal(10, ids.Length);
                Assert.All(ids, id => Assert.Equal(1u << 22, id & (3u << 22)));   // bit 22 set, bit 23 (text images) clear
                Assert.All(ids, id => Assert.True(spinnerIds.Add(id), $"duplicate spinner id {id}"));
            }
        }

        Assert.Empty(tileIds.Intersect(spinnerIds));
        Assert.All(tileIds, id => Assert.Equal(0u, id & (3u << 22)));
    }

    private static List<(byte[] Pixels, int W, int H, int Cols, int Rows)> Frames(string theme, string spinner, int width, int height)
    {
        var styles = Styles(theme);
        var shape = Get<object>(styles, spinner);
        var frames = FramesType.GetMethod("Create")!.Invoke(null, [shape, 0, Array.IndexOf(Spinners, spinner), Cell(width, height)])!;
        return [.. ((System.Collections.IEnumerable)Get<object>(frames, "Frames")).Cast<object>().Select(tile =>
        {
            var image = Get<object>(tile, "Image");
            return (Get<byte[]>(image, "Pixels"), Get<int>(image, "Width"), Get<int>(image, "Height"), Get<int>(tile, "Cols"), Get<int>(tile, "Rows"));
        })];
    }

    /// <summary>The direction of the arc-coloured pixels from the frame's centre, in degrees
    /// clockwise from up.</summary>
    private static double ArcAngle(byte[] pixels, int w, int h, Color arc)
    {
        double sx = 0, sy = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (Error(pixels, w, x, y, arc) > 40) continue;
            sx += x + 0.5 - w / 2.0;
            sy += y + 0.5 - h / 2.0;
        }
        return (Math.Atan2(sx, -sy) * 180 / Math.PI + 360) % 360;
    }

    private static int Error(byte[] pixels, int w, int x, int y, Color c)
    {
        var i = (y * w + x) * 3;
        return Math.Max(Math.Abs(pixels[i] - c.R), Math.Max(Math.Abs(pixels[i + 1] - c.G), Math.Abs(pixels[i + 2] - c.B)));
    }
}
