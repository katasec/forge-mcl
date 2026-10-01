namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 (G6): the eight edge tiles of a card, cut from one rendered template card. Edge tiles
// come from the middle of each side, where the shape no longer changes along the edge, so
// repeating them is seamless; the seam check proves that by comparing them with the cells next to
// the corners.

/// <summary>One image tile spanning Cols x Rows cells.</summary>
internal sealed record Tile(RgbImage Image, int Cols, int Rows);

internal sealed record CardTiles(
    Tile TopLeft, Tile Top, Tile TopRight, Tile Left, Tile Right, Tile BottomLeft, Tile Bottom, Tile BottomRight,
    double SeamLevels, double EdgeLevels, double InteriorLevels)
{
    /// <summary>Largest output-level difference treated as invisible at a tile joint.</summary>
    public const double CleanLevels = 1.0;

    /// <summary>The tiles in image-id order (the order CardFrame names them in).</summary>
    public IReadOnlyList<Tile> All => [TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight];

    /// <summary>Seamless joints, invisible shadow cut-off at the ring's outer edge, plain interior.</summary>
    public bool IsClean => SeamLevels <= CleanLevels && EdgeLevels <= CleanLevels && InteriorLevels <= CleanLevels;

    public static CardTiles Render(CardEdges edges, RingLayout layout)
    {
        var grid = TemplateGrid.For(layout);
        var template = DrawCard(edges, layout, grid);
        var tiles = Cut(template, layout, grid);
        return tiles with
        {
            SeamLevels = SeamError(template, tiles, layout, grid),
            EdgeLevels = OuterEdgeError(template, Rgb.From(edges.Surface)),
            InteriorLevels = InteriorError(template, layout, grid, Rgb.From(edges.CardSurface)),
        };
    }

    // ── Template ────────────────────────────────────────────────────────────────────────────

    /// <summary>The template in cells: the ring, plus enough interior that the far edges cannot
    /// reach the middle cells the edge tiles are cut from.</summary>
    private sealed record TemplateGrid(int InteriorCols, int InteriorRows, int Cols, int Rows)
    {
        public static TemplateGrid For(RingLayout l)
        {
            var reach = l.Radius + l.Shadows.Max(s => 3 * s.Sigma + Math.Abs(s.Dy)) + l.SideInset + l.BottomInset;
            var cols = 2 * (int)Math.Ceiling(reach / l.Cell.Width) + 3;
            var rows = 2 * (int)Math.Ceiling(reach / l.Cell.Height) + 3;
            return new(cols, rows, 2 * l.SideCols + cols, l.TopRows + rows + l.BottomRows);
        }
    }

    /// <summary>Shadows on the surface, then the border shape, then the card fill inset by the hairline.</summary>
    private static RgbImage DrawCard(CardEdges edges, RingLayout l, TemplateGrid grid)
    {
        int w = grid.Cols * l.Cell.Width, h = grid.Rows * l.Cell.Height;
        var border = new RectF(l.SideInset, l.TopInset, w - l.SideInset, h - l.BottomInset);
        var canvas = new LinearCanvas(w, h, Rgb.From(edges.Surface));
        foreach (var shadow in l.Shadows)
        {
            var mask = Shapes.RoundedRect(w, h, border.Offset(0, shadow.Dy), l.Radius);
            Shapes.Blur(mask, w, h, shadow.Sigma);
            canvas.Composite(mask, shadow.Color, shadow.Alpha);
        }
        canvas.Composite(Shapes.RoundedRect(w, h, border, l.Radius), Rgb.From(edges.CardBorder), 1);
        canvas.Composite(Shapes.RoundedRect(w, h, border.Inset(l.Hairline), Math.Max(0, l.Radius - l.Hairline)),
            Rgb.From(edges.CardSurface), 1);
        return canvas.ToSrgb();
    }

    private static CardTiles Cut(RgbImage t, RingLayout l, TemplateGrid g)
    {
        int midCol = l.SideCols + g.InteriorCols / 2, midRow = l.TopRows + g.InteriorRows / 2;
        int rightCol = l.SideCols + g.InteriorCols, bottomRow = l.TopRows + g.InteriorRows;
        Tile Take(int col, int row, int cols, int rows) =>
            new(t.Crop(col * l.Cell.Width, row * l.Cell.Height, cols * l.Cell.Width, rows * l.Cell.Height), cols, rows);
        return new CardTiles(
            Take(0, 0, l.SideCols, l.TopRows),
            Take(midCol, 0, 1, l.TopRows),
            Take(rightCol, 0, l.SideCols, l.TopRows),
            Take(0, midRow, l.SideCols, 1),
            Take(rightCol, midRow, l.SideCols, 1),
            Take(0, bottomRow, l.SideCols, l.BottomRows),
            Take(midCol, bottomRow, 1, l.BottomRows),
            Take(rightCol, bottomRow, l.SideCols, l.BottomRows),
            0, 0, 0);
    }

    // ── Checks (max channel difference, 0..255) ─────────────────────────────────────────────

    /// <summary>Each edge tile against the template cells right next to both of its corners.</summary>
    private static double SeamError(RgbImage t, CardTiles tiles, RingLayout l, TemplateGrid g)
    {
        int cw = l.Cell.Width, ch = l.Cell.Height;
        int firstCol = l.SideCols, lastCol = l.SideCols + g.InteriorCols - 1;
        int firstRow = l.TopRows, lastRow = l.TopRows + g.InteriorRows - 1;
        int rightCol = l.SideCols + g.InteriorCols, bottomRow = l.TopRows + g.InteriorRows;
        double Diff(Tile tile, int col, int row) => MaxDifference(tile.Image, t.Crop(col * cw, row * ch, tile.Image.Width, tile.Image.Height));
        return new[]
        {
            Diff(tiles.Top, firstCol, 0), Diff(tiles.Top, lastCol, 0),
            Diff(tiles.Bottom, firstCol, bottomRow), Diff(tiles.Bottom, lastCol, bottomRow),
            Diff(tiles.Left, 0, firstRow), Diff(tiles.Left, 0, lastRow),
            Diff(tiles.Right, rightCol, firstRow), Diff(tiles.Right, rightCol, lastRow),
        }.Max();
    }

    /// <summary>The template's outermost pixels against the plain surface.</summary>
    private static double OuterEdgeError(RgbImage t, Rgb surface)
    {
        double max = 0;
        for (var x = 0; x < t.Width; x++) max = Math.Max(max, Math.Max(PixelError(t, x, 0, surface), PixelError(t, x, t.Height - 1, surface)));
        for (var y = 0; y < t.Height; y++) max = Math.Max(max, Math.Max(PixelError(t, 0, y, surface), PixelError(t, t.Width - 1, y, surface)));
        return max;
    }

    /// <summary>The interior's four corner cells against plain CardSurface (they are text cells).</summary>
    private static double InteriorError(RgbImage t, RingLayout l, TemplateGrid g, Rgb card)
    {
        int cw = l.Cell.Width, ch = l.Cell.Height;
        (int Col, int Row)[] corners =
        [
            (l.SideCols, l.TopRows), (l.SideCols + g.InteriorCols - 1, l.TopRows),
            (l.SideCols, l.TopRows + g.InteriorRows - 1), (l.SideCols + g.InteriorCols - 1, l.TopRows + g.InteriorRows - 1),
        ];
        double max = 0;
        foreach (var (col, row) in corners)
            for (var y = row * ch; y < (row + 1) * ch; y++)
            for (var x = col * cw; x < (col + 1) * cw; x++)
                max = Math.Max(max, PixelError(t, x, y, card));
        return max;
    }

    private static double MaxDifference(RgbImage a, RgbImage b)
    {
        var max = 0;
        for (var i = 0; i < a.Pixels.Length; i++) max = Math.Max(max, Math.Abs(a.Pixels[i] - b.Pixels[i]));
        return max;
    }

    private static double PixelError(RgbImage t, int x, int y, Rgb c)
    {
        var i = (y * t.Width + x) * 3;
        return Math.Max(Math.Abs(t.Pixels[i] - c.R), Math.Max(Math.Abs(t.Pixels[i + 1] - c.G), Math.Abs(t.Pixels[i + 2] - c.B)));
    }
}
