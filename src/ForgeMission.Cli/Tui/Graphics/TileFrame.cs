using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56: a shape — its content inside the ring of tile placeholder cells. The ring (and its
// plain padding) is the Padder's inset, so the content keeps ordinary layout; this visual paints
// the inset cells: tiles in the ring band, plain fill elsewhere. A cap set is a ring with no top or
// bottom rows, so the same painting puts its caps beside one row of content. The caller sets the
// alignment (a card stretches; a user message is right-aligned at its content's width).
//
// A frame given two sets picks per layout: the one-line set when its content measures one row
// at the width left inside it, else the multi-line set (the user message: a capped pill, or the
// ring once it wraps). Both sets must have the same side columns, so the choice never changes the
// content's width.
//
// A frame inside a DocumentFlow block is arranged at its full size (also when scrolled partly off
// screen), so its tile rows count from its real top.
internal sealed class TileFrame : Padder
{
    private const int Inside = -1;

    private readonly TileSet _oneLine;
    private readonly TileSet? _multiLine;
    private readonly Style _fill;
    private TileSet _active;

    public TileFrame(Visual content, TileSet tiles, Style fill, Align alignment)
        : this(content, tiles, null, fill, alignment)
    {
    }

    private TileFrame(Visual content, TileSet oneLine, TileSet? multiLine, Style fill, Align alignment)
    {
        _oneLine = oneLine;
        _multiLine = multiLine;
        _active = oneLine;
        _fill = fill;
        Content = content;
        HorizontalAlignment = alignment;
    }

    /// <summary>A frame that is <paramref name="oneLine"/> while its content fits one row and
    /// <paramref name="multiLine"/> once it does not.</summary>
    public static TileFrame OneLineOr(Visual content, TileSet oneLine, TileSet multiLine, Style fill, Align alignment) =>
        new(content, oneLine, multiLine, fill, alignment);

    protected override Thickness Inset => InsetOf(_active.Layout);

    protected override SizeHints MeasureCore(in LayoutConstraints constraints)
    {
        if (_multiLine is not null) _active = FitsOneRow(constraints) ? _oneLine : _multiLine;
        return base.MeasureCore(in constraints);
    }

    protected override void RenderOverride(CellBuffer buffer)
    {
        var b = Bounds;
        for (var y = b.Y; y < b.Y + b.Height; y++)
        for (var x = b.X; x < b.X + b.Width; x++)
            PaintCell(buffer, b, x, y);
    }

    private static Thickness InsetOf(RingLayout l) => new(l.SideCols + l.PadCols, l.TopRows + l.PadTop,
        l.SideCols + l.PadCols, l.BottomRows + l.PadBottom);

    /// <summary>Whether the content is one row tall at the width the one-line set leaves it.</summary>
    private bool FitsOneRow(in LayoutConstraints constraints)
    {
        if (Content is null) return true;
        var inset = InsetOf(_oneLine.Layout);
        var width = constraints.MaxWidth == int.MaxValue ? int.MaxValue : Math.Max(0, constraints.MaxWidth - inset.Horizontal);
        var inner = new LayoutConstraints(0, width, 0, int.MaxValue);
        return Content.Measure(in inner).Natural.Height <= 1;
    }

    /// <summary>A ring cell shows its tile's placeholder; any other cell is plain fill.</summary>
    private void PaintCell(CellBuffer buffer, Rectangle bounds, int x, int y)
    {
        var (index, row, col) = TileAt(_active.Layout, bounds, x, y);
        if (index == Inside)
            buffer.SetCell(x, y, new System.Text.Rune(' '), _fill);
        else
            buffer.WriteText(x, y, KittyImages.Cell(row, col), _fill.WithForeground(KittyImages.IdColor(_active.Ids[index])));
    }

    /// <summary>Which tile slot covers cell (x, y), and which cell of that tile; <see cref="Inside"/>
    /// inside the ring. Slot order: top-left, top, top-right, left, right, bottom-left, bottom,
    /// bottom-right (<see cref="RingTiles.All"/>). In a frame narrower than both side bands the
    /// left band wins, so the right tiles are clipped.</summary>
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
