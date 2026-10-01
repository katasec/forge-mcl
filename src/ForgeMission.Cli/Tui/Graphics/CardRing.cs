namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56: the one card tile set of a forge chat run — solved and drawn once at start-up from the
// theme's card tokens and the cell size, sent once on the TUI's first tick, and named by every card
// through its image ids. Ids are derived from (theme, cell size), so a later run in the same
// window with another theme or display never re-sends an id at a different size (re-sending one
// left stale placements, tui-graphics.md).
internal sealed class CardRing
{
    private const int ThemeBits = 1, WidthBits = 9, HeightBits = 11, SlotBits = 3;

    private CardRing(RingLayout layout, CardTiles tiles, uint[] ids) => (Layout, Tiles, Ids) = (layout, tiles, ids);

    public RingLayout Layout { get; }

    public CardTiles Tiles { get; }

    /// <summary>The image id of each tile, in <see cref="CardTiles.All"/> order.</summary>
    public IReadOnlyList<uint> Ids { get; }

    /// <summary>The ring column that holds the card's border line.</summary>
    public int BorderCol => Layout.SideInset / Layout.Cell.Width;

    public static CardRing Create(CardEdges edges, int themeSlot, CellSize cell)
    {
        var layout = RingGeometry.Solve(edges, cell);
        return new CardRing(layout, CardTiles.Render(edges, layout), ImageIds(themeSlot, cell.Width, cell.Height));
    }

    /// <summary>Sends the eight tiles. Must run after the TUI has entered the alternate screen.</summary>
    public void Transmit()
    {
        var tiles = Tiles.All;
        for (var i = 0; i < tiles.Count; i++)
            KittyImages.Transmit(Ids[i], Png.Encode(tiles[i].Image), tiles[i].Cols, tiles[i].Rows);
    }

    /// <summary>Eight ids packed as theme | cell width | cell height | tile slot, inside the 24 bits
    /// a placeholder's foreground colour carries. Distinct inputs never share an id.</summary>
    internal static uint[] ImageIds(int themeSlot, int cellWidth, int cellHeight)
    {
        Require(themeSlot, 0, (1 << ThemeBits) - 1, nameof(themeSlot));
        Require(cellWidth, 1, (1 << WidthBits) - 1, nameof(cellWidth));
        Require(cellHeight, 1, (1 << HeightBits) - 1, nameof(cellHeight));
        var first = (((uint)themeSlot << WidthBits | (uint)cellWidth) << HeightBits | (uint)cellHeight) << SlotBits;
        return [.. Enumerable.Range(0, 1 << SlotBits).Select(slot => first | (uint)slot)];
    }

    private static void Require(int value, int min, int max, string name)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(name, value, $"Card image ids need {name} in {min}..{max}.");
    }
}
