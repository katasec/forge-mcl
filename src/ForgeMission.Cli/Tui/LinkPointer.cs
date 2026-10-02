using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;

namespace ForgeMission.Cli.Tui;

// Phase 56 Task 5 (.md a { cursor: pointer }): a hand while the mouse is over a link. XenoAtom can't
// say which inline is under the pointer, so each reply body carries a probe: an overlay that, every
// time the body renders, records which of its visible cells carry a link (XenoCells, the Type-2
// exception). A pointer position is a link when a probe still on the screen (under the screen's
// root; the transcript detaches blocks it scrolls away) and standing where it last rendered
// recorded that cell; ChatScreen forgets the probe of a block it removes. The shape is checked
// when the pointer moves and on every UI tick (content can scroll under a still pointer), and an
// escape is written only when it changes. The exits put the default back (TerminalPointer).
internal sealed class LinkPointer(Visual root, Action<string> write)
{
    private readonly Dictionary<Probe, (Rectangle Bounds, HashSet<(int X, int Y)> Cells)> _links = [];
    private (int X, int Y)? _pointer;
    private bool _hand;

    /// <summary>The overlay one reply body carries.</summary>
    public Visual NewProbe() => new Probe(this);

    /// <summary>The pointer moved to (x, y), in screen cells.</summary>
    public void PointerAt(int x, int y)
    {
        _pointer = (x, y);
        Recheck();
    }

    /// <summary>Sets the shape for where the pointer is now.</summary>
    public void Recheck()
    {
        var hand = _pointer is { } at && IsLink(at.X, at.Y);
        if (hand == _hand) return;
        _hand = hand;
        write(hand ? TerminalPointer.HandEscape : TerminalPointer.DefaultEscape);
    }

    /// <summary>The probe's body left the screen (ChatScreen removed its block).</summary>
    public void Forget(Visual probe) => _links.Remove((Probe)probe);

    /// <summary>Whether a link is drawn at (x, y): recorded by a probe still where it rendered.</summary>
    internal bool IsLink(int x, int y) => _links.Any(entry =>
        entry.Value.Cells.Contains((x, y)) && entry.Key.Bounds == entry.Value.Bounds && OnScreen(entry.Key));

    private bool OnScreen(Visual probe)
    {
        var top = probe;
        while (top.Parent is { } parent) top = parent;
        return top == root;
    }

    /// <summary>A probe rendered: its link cells inside <paramref name="area"/> replace what it had
    /// there; cells outside it stay while the probe has not moved.</summary>
    private void Record(Probe probe, Rectangle bounds, Rectangle area, IEnumerable<(int X, int Y)> cells)
    {
        if (!_links.TryGetValue(probe, out var entry) || entry.Bounds != bounds)
            entry = (bounds, []);
        entry.Cells.RemoveWhere(cell => area.Contains(cell.X, cell.Y));
        entry.Cells.UnionWith(cells);
        _links[probe] = entry;
    }

    private sealed class Probe : Visual
    {
        private readonly LinkPointer _owner;

        public Probe(LinkPointer owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
            HorizontalAlignment = Align.Stretch;
            VerticalAlignment = Align.Stretch;
        }

        protected override SizeHints MeasureCore(in LayoutConstraints constraints) => SizeHints.Fixed(new Size(0, 0));

        protected override void RenderOverride(CellBuffer buffer)
        {
            var clip = XenoCells.Clip(buffer);
            var b = Bounds;
            var x0 = Math.Max(b.X, clip.X);
            var y0 = Math.Max(b.Y, clip.Y);
            var area = new Rectangle(x0, y0,
                Math.Max(0, Math.Min(b.X + b.Width, clip.X + clip.Width) - x0),
                Math.Max(0, Math.Min(b.Y + b.Height, clip.Y + clip.Height) - y0));
            var cells = new List<(int, int)>();
            for (var y = area.Y; y < area.Y + area.Height; y++)
            for (var x = area.X; x < area.X + area.Width; x++)
                if (XenoCells.Read(buffer, x, y).Link) cells.Add((x, y));
            _owner.Record(this, b, area, cells);
        }
    }
}
