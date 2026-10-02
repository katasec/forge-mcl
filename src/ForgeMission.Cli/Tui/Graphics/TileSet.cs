namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56: one shape's tile set for a forge chat run — solved and drawn once at start-up from the
// theme's tokens and the cell size, sent once on the TUI's first tick, and named by every frame of
// that shape through its image ids. A ring set fills all eight slots; a cap set is a ring with no
// top or bottom rows and fills only the Left and Right slots. Ids are derived from (theme, set,
// cell size), so a later run in the same window with another theme or display never re-sends an
// id at a different size (re-sending one left stale placements, tui-graphics.md).
internal sealed class TileSet
{
    private const int ThemeBits = 1, WidthBits = 7, HeightBits = 8, SetBits = 3, SlotBits = 3;
    private const int LeftSlot = 3, RightSlot = 4;

    private TileSet(RingLayout layout, Tile?[] tiles, uint[] ids) => (Layout, Tiles, Ids) = (layout, tiles, ids);

    public RingLayout Layout { get; }

    /// <summary>The tile in each slot (<see cref="RingTiles.All"/> order); null where a cap set has
    /// none. A cap layout has no top or bottom rows, so TileFrame only ever names the Left and Right
    /// slots of a cap set.</summary>
    public IReadOnlyList<Tile?> Tiles { get; }

    /// <summary>The image id of each slot.</summary>
    public IReadOnlyList<uint> Ids { get; }

    /// <summary>The ring column that holds the shape's border line.</summary>
    public int BorderCol => Layout.SideInset / Layout.Cell.Width;

    public static TileSet Ring(RingShape shape, int themeSlot, int set, CellSize cell)
    {
        var layout = RingGeometry.Solve(shape, cell);
        return new TileSet(layout, [.. RingTiles.Render(shape, layout).All], ImageIds(themeSlot, set, cell.Width, cell.Height));
    }

    public static TileSet Caps(CapShape shape, int themeSlot, int set, CellSize cell)
    {
        var caps = CapTiles.Render(shape, cell);
        var tiles = new Tile?[1 << SlotBits];
        (tiles[LeftSlot], tiles[RightSlot]) = (caps.Left, caps.Right);
        return new TileSet(RingLayout.Caps(cell), tiles, ImageIds(themeSlot, set, cell.Width, cell.Height));
    }

    /// <summary>Sends the set's tiles. Must run after the TUI has entered the alternate screen.</summary>
    public void Transmit()
    {
        for (var i = 0; i < Tiles.Count; i++)
        {
            if (Tiles[i] is { } tile)
                KittyImages.Transmit(Ids[i], Png.Encode(tile.Image), tile.Cols, tile.Rows);
        }
    }

    /// <summary>Eight ids packed as theme | cell width | cell height | set | slot, inside the 24 bits
    /// a placeholder's foreground colour carries (22 used). Distinct inputs never share an id.</summary>
    internal static uint[] ImageIds(int themeSlot, int set, int cellWidth, int cellHeight)
    {
        Require(themeSlot, 0, (1 << ThemeBits) - 1, nameof(themeSlot));
        Require(set, 0, (1 << SetBits) - 1, nameof(set));
        Require(cellWidth, 1, (1 << WidthBits) - 1, nameof(cellWidth));
        Require(cellHeight, 1, (1 << HeightBits) - 1, nameof(cellHeight));
        var cellPart = ((uint)themeSlot << WidthBits | (uint)cellWidth) << HeightBits | (uint)cellHeight;
        var first = (cellPart << SetBits | (uint)set) << SlotBits;
        return [.. Enumerable.Range(0, 1 << SlotBits).Select(slot => first | (uint)slot)];
    }

    private static void Require(int value, int min, int max, string name)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(name, value, $"Image ids need {name} in {min}..{max}.");
    }
}
