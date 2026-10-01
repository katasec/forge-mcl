using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Rendering;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56: a card — its content inside the ring of edge-tile placeholder cells. The ring is the
// padding of a Padder, so the content keeps ordinary layout; this visual paints the padding cells:
// tiles in the ring band, plain card fill elsewhere. A card spans the full width it is given,
// whatever its content, and has one more plain row (CardPaddingRows) below its content than the
// ring gives: the downward shadow puts the bottom border high in its tile, and the mockup's card
// padding is equal top and bottom. A card is one DocumentFlow block, so it is
// always arranged at its full size (also when scrolled partly off screen) and its tile rows count
// from its real top.
internal sealed class CardFrame : Padder
{
    private const int Inside = -1;

    private readonly CardRing _ring;
    private readonly Style _fill;

    public CardFrame(Visual content, CardRing ring, Style fill)
    {
        _ring = ring;
        _fill = fill;
        Content = content;
        HorizontalAlignment = Align.Stretch;
        var l = ring.Layout;
        Padding = new Thickness(l.SideCols + l.PadCols, l.TopRows + l.PadTop, l.SideCols + l.PadCols,
            l.BottomRows + l.PadBottom + ForgeTheme.CardPaddingRows);
    }

    protected override void RenderOverride(CellBuffer buffer)
    {
        var b = Bounds;
        for (var y = b.Y; y < b.Y + b.Height; y++)
        for (var x = b.X; x < b.X + b.Width; x++)
            PaintCell(buffer, b, x, y);
    }

    /// <summary>A ring cell shows its tile's placeholder; any other cell is plain card fill.</summary>
    private void PaintCell(CellBuffer buffer, Rectangle bounds, int x, int y)
    {
        var (index, row, col) = TileAt(_ring.Layout, bounds, x, y);
        if (index == Inside)
            buffer.SetCell(x, y, new System.Text.Rune(' '), _fill);
        else
            buffer.WriteText(x, y, KittyImages.Cell(row, col), _fill.WithForeground(KittyImages.IdColor(_ring.Ids[index])));
    }

    /// <summary>Which tile covers cell (x, y), and which cell of that tile; <see cref="Inside"/>
    /// inside the ring. Index order: top-left, top, top-right, left, right, bottom-left, bottom,
    /// bottom-right (<see cref="CardTiles.All"/>).</summary>
    private static (int Index, int Row, int Col) TileAt(RingLayout l, Rectangle b, int x, int y)
    {
        var (band, col) = x < b.X + l.SideCols ? (0, x - b.X)
            : x >= b.X + b.Width - l.SideCols ? (2, x - (b.X + b.Width - l.SideCols))
            : (1, 0);
        var (level, row) = y < b.Y + l.TopRows ? (0, y - b.Y)
            : y >= b.Y + b.Height - l.BottomRows ? (2, y - (b.Y + b.Height - l.BottomRows))
            : (1, 0);
        return (level, band) switch
        {
            (1, 1) => (Inside, 0, 0),
            (0, _) => (band, row, col),
            (1, 0) => (3, 0, col),
            (1, 2) => (4, 0, col),
            _ => (5 + band, row, col),
        };
    }
}
