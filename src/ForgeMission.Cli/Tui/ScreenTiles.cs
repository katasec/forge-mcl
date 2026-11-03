using ForgeMission.Cli.Tui.Graphics;

namespace ForgeMission.Cli.Tui;

// Phase 56: the tile-set registry ChatScreen holds — one tile set per shape on the screen, drawn
// from ForgeStyles at the cell size the terminal answered on the TUI's first tick, and sent once
// then. The set number is part of every image id, so the numbers below are fixed; set 5 is unused.
// Task 5 adds the card's hover edge (set 7) and the spinners' frames: 4 ring sets × 8 +
// 2 cap sets × 2 + 8 hover tiles + 2 spinners × 10 frames = 64 images per session.
internal sealed record ScreenTiles(
    TileSet Card, TileSet CodeBlock, TileSet UserRing, TileSet Composer, TileSet UserCaps, TileSet Tool,
    TileSet CardHover, SpinnerFrames ProgressSpinner, SpinnerFrames ToolSpinner)
{
    public IReadOnlyList<TileSet> All => [Card, CodeBlock, UserRing, Composer, UserCaps, Tool, CardHover];

    public static ScreenTiles Create(ForgeStyles styles, CellSize cell)
    {
        var theme = styles.ImageIdSlot;
        return new ScreenTiles(
            TileSet.Ring(styles.CardShape, theme, 0, cell),
            TileSet.Ring(styles.CodeBlockShape, theme, 1, cell),
            TileSet.Ring(styles.UserShape, theme, 2, cell),
            TileSet.Ring(styles.ComposerShape, theme, 3, cell),
            TileSet.Caps(styles.UserCaps, theme, 4, cell),
            TileSet.Caps(styles.ToolCaps, theme, 6, cell),
            TileSet.Ring(styles.CardHoverShape, theme, 7, cell),
            SpinnerFrames.Create(styles.ProgressSpinner, theme, 0, cell),
            SpinnerFrames.Create(styles.ToolSpinner, theme, 1, cell));
    }

    /// <summary>Sends every set and every spinner frame. Must run after the TUI has entered the
    /// alternate screen.</summary>
    public void Transmit()
    {
        foreach (var set in All) set.Transmit();
        ProgressSpinner.Transmit();
        ToolSpinner.Transmit();
    }
}
