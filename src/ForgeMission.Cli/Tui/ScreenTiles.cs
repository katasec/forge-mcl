using ForgeMission.Cli.Tui.Graphics;

namespace ForgeMission.Cli.Tui;

// Phase 56: the tile-set registry ChatScreen holds — one tile set per shape on the screen, drawn
// from ForgeStyles at the cell size the terminal answered on the TUI's first tick, and sent once
// then (4 ring sets × 8 tiles + 3 cap sets × 2 = 38 images per session). The set number is part
// of every image id, so the order below is fixed.
internal sealed record ScreenTiles(
    TileSet Card, TileSet CodeBlock, TileSet UserRing, TileSet Composer, TileSet UserCaps, TileSet Approved, TileSet Tool)
{
    public IReadOnlyList<TileSet> All => [Card, CodeBlock, UserRing, Composer, UserCaps, Approved, Tool];

    public static ScreenTiles Create(ForgeStyles styles, CellSize cell)
    {
        var theme = styles.ImageIdSlot;
        return new ScreenTiles(
            TileSet.Ring(styles.CardShape, theme, 0, cell),
            TileSet.Ring(styles.CodeBlockShape, theme, 1, cell),
            TileSet.Ring(styles.UserShape, theme, 2, cell),
            TileSet.Ring(styles.ComposerShape, theme, 3, cell),
            TileSet.Caps(styles.UserCaps, theme, 4, cell),
            TileSet.Caps(styles.ApprovedCaps, theme, 5, cell),
            TileSet.Caps(styles.ToolCaps, theme, 6, cell));
    }

    /// <summary>Sends every set. Must run after the TUI has entered the alternate screen.</summary>
    public void Transmit()
    {
        foreach (var set in All) set.Transmit();
    }
}
