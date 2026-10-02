namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 (G6): the eight edge tiles of a ring shape (card, code block, multi-line user message,
// composer), cut from one rendered template. Edge tiles come from the middle of each side, where
// the shape no longer changes along the edge, so repeating them is seamless. TileSetTests proves
// that on the template: each edge tile matches the cells next to the corners, a shadow has faded
// at the ring's edge, the interior is plain fill.

/// <summary>One image tile spanning Cols x Rows cells.</summary>
internal sealed record Tile(RgbImage Image, int Cols, int Rows);

/// <summary>The tiles, and the template they were cut from (the ring around plain interior
/// cells), which the tests check.</summary>
internal sealed record RingTiles(
    Tile TopLeft, Tile Top, Tile TopRight, Tile Left, Tile Right, Tile BottomLeft, Tile Bottom, Tile BottomRight,
    RgbImage Template)
{
    /// <summary>The tiles in slot order (the order TileFrame names them in).</summary>
    public IReadOnlyList<Tile> All => [TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight];

    public static RingTiles Render(RingShape shape, RingLayout layout)
    {
        var grid = TemplateGrid.For(layout);
        return Cut(Draw(shape, layout, grid), layout, grid);
    }

    /// <summary>The template in cells: the ring, plus enough interior that the far edges cannot
    /// reach the middle cells the edge tiles are cut from.</summary>
    private sealed record TemplateGrid(int InteriorCols, int InteriorRows, int Cols, int Rows)
    {
        public static TemplateGrid For(RingLayout l)
        {
            var shadowReach = l.Shadows.Length == 0 ? 0 : l.Shadows.Max(s => 3 * s.Sigma + Math.Abs(s.Dy));
            var reach = l.Radius + shadowReach + l.Glow + l.SideInset + l.BottomInset;
            var cols = 2 * (int)Math.Ceiling(reach / l.Cell.Width) + 3;
            var rows = 2 * (int)Math.Ceiling(reach / l.Cell.Height) + 3;
            return new(cols, rows, 2 * l.SideCols + cols, l.TopRows + rows + l.BottomRows);
        }
    }

    /// <summary>Shadows and glow on the surface, then the border shape, then the fill inset by
    /// the hairline (or the fill alone when there is no border line).</summary>
    private static RgbImage Draw(RingShape shape, RingLayout l, TemplateGrid grid)
    {
        int w = grid.Cols * l.Cell.Width, h = grid.Rows * l.Cell.Height;
        var border = new RectF(l.SideInset, l.TopInset, w - l.SideInset, h - l.BottomInset);
        var canvas = new LinearCanvas(w, h, Rgb.From(shape.Surface));
        foreach (var shadow in l.Shadows)
        {
            var mask = Shapes.RoundedRect(w, h, border.Offset(0, shadow.Dy), l.Radius);
            Shapes.Blur(mask, w, h, shadow.Sigma);
            canvas.Composite(mask, shadow.Color, shadow.Alpha);
        }
        if (shape.Glow is { } glow && l.Glow > 0)
            canvas.Composite(Shapes.RoundedRect(w, h, border.Inset(-l.Glow), l.Radius + l.Glow), Rgb.From(glow.Color), glow.Alpha);
        if (l.Hairline > 0)
            canvas.Composite(Shapes.RoundedRect(w, h, border, l.Radius), Rgb.From(shape.Border), 1);
        canvas.Composite(Shapes.RoundedRect(w, h, border.Inset(l.Hairline), Math.Max(0, l.Radius - l.Hairline)),
            Rgb.From(shape.Fill), 1);
        return canvas.ToSrgb();
    }

    private static RingTiles Cut(RgbImage t, RingLayout l, TemplateGrid g)
    {
        int midCol = l.SideCols + g.InteriorCols / 2, midRow = l.TopRows + g.InteriorRows / 2;
        int rightCol = l.SideCols + g.InteriorCols, bottomRow = l.TopRows + g.InteriorRows;
        Tile Take(int col, int row, int cols, int rows) =>
            new(t.Crop(col * l.Cell.Width, row * l.Cell.Height, cols * l.Cell.Width, rows * l.Cell.Height), cols, rows);
        return new RingTiles(
            Take(0, 0, l.SideCols, l.TopRows),
            Take(midCol, 0, 1, l.TopRows),
            Take(rightCol, 0, l.SideCols, l.TopRows),
            Take(0, midRow, l.SideCols, 1),
            Take(rightCol, midRow, l.SideCols, 1),
            Take(0, bottomRow, l.SideCols, l.BottomRows),
            Take(midCol, bottomRow, 1, l.BottomRows),
            Take(rightCol, bottomRow, l.SideCols, l.BottomRows),
            t);
    }
}
