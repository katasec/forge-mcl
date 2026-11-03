using XenoAtom.Terminal.UI;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 Task 3: the two caps of a one-row pill (one-line user message, tool chip),
// cut from one rendered pill. The pill is one cell tall with half-round ends (radius = half the
// cell height); each cap is ForgeTheme.PillCapCols columns: the round end at the outside, flat fill
// towards the text. The text cells between the caps carry the same fill as their background, and
// TileSetTests proves the cap's inner pixel column is that fill (no step at the join).

/// <summary>What a pill's caps are drawn from: ForgeTheme tokens.</summary>
internal sealed record CapShape(Color Surface, Color Fill);

/// <summary>The caps, and the pill template they were cut from, which the tests check.</summary>
internal sealed record CapTiles(Tile Left, Tile Right, RgbImage Template)
{
    /// <summary>Plain text columns between the caps on the template.</summary>
    private const int InteriorCols = 3;

    public static CapTiles Render(CapShape shape, CellSize cell)
    {
        var cols = 2 * ForgeTheme.PillCapCols + InteriorCols;
        var template = Draw(shape, cols * cell.Width, cell.Height);
        var capWidth = ForgeTheme.PillCapCols * cell.Width;
        return new CapTiles(
            new Tile(template.Crop(0, 0, capWidth, cell.Height), ForgeTheme.PillCapCols, 1),
            new Tile(template.Crop(template.Width - capWidth, 0, capWidth, cell.Height), ForgeTheme.PillCapCols, 1),
            template);
    }

    /// <summary>The pill on the surface.</summary>
    private static RgbImage Draw(CapShape shape, int w, int h)
    {
        var canvas = new LinearCanvas(w, h, Rgb.From(shape.Surface));
        canvas.Composite(Shapes.RoundedRect(w, h, new RectF(0, 0, w, h), h / 2.0), Rgb.From(shape.Fill), 1);
        return canvas.ToSrgb();
    }
}
