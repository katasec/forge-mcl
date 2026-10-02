using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Animation;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;

namespace ForgeMission.Cli.Tui;

// Phase 56 Task 5 (.fade): streamed text fades in. An overlay drawn right after a reply's body, over
// the same cells. When a live delta changes the body (MarkDelta), the next render compares every
// visible cell with the snapshot taken at the previous delta; each cell whose glyph changed starts
// its own fade, so a delta never restarts words already shown on the line being typed. A fading
// cell's text colour moves from the cell's own background to its own colour over ForgeTheme.FadeMs
// (CSS ease-out, mixed in sRGB) and is written opaque, keeping the glyph and its link. Image
// placeholder cells (headings, code-block edges) are never touched: their colour is an image id.
// Cells are read through XenoCells (the Type-2 exception). The snapshot is in body coordinates, so
// scrolling between deltas changes nothing. While nothing fades the overlay asks for no frames.
internal sealed class FadeIn : Visual, IAnimatedVisual
{
    private readonly Func<long> _clock;
    // At the last delta: the glyph shown in each body cell (row, col).
    private readonly Dictionary<(int Row, int Col), CellFacts> _snapshot = [];
    // Each cell still fading, and when its fade started.
    private readonly Dictionary<(int Row, int Col), long> _fades = [];
    private bool _deltaPending;
    private long _next = long.MaxValue;

    public FadeIn(Func<long> clock)
    {
        _clock = clock;
        IsHitTestVisible = false;
        HorizontalAlignment = Align.Stretch;
        VerticalAlignment = Align.Stretch;
    }

    /// <summary>A live delta changed the body: the next render starts fades for the cells it changed.</summary>
    public void MarkDelta() => _deltaPending = true;

    /// <summary>The reply stopped streaming: no more deltas, so the snapshot goes. Fades already
    /// running finish.</summary>
    public void Settle()
    {
        _deltaPending = false;
        _snapshot.Clear();
    }

    /// <summary>Whether any cell is fading.</summary>
    public bool Fading => _fades.Count > 0;

    long IAnimatedVisual.NextAnimationTick => _next;

    bool IAnimatedVisual.AdvanceAnimation(long timestamp)
    {
        if (_fades.Count == 0)
        {
            _next = long.MaxValue;
            return false;
        }
        foreach (var cell in _fades.Where(fade => !Motion.Fading(fade.Value, timestamp)).Select(fade => fade.Key).ToList())
            _fades.Remove(cell);
        _next = _fades.Count == 0 ? long.MaxValue : timestamp + Motion.Ticks(ForgeTheme.FadeFrameMs);
        return true;
    }

    protected override SizeHints MeasureCore(in LayoutConstraints constraints) => SizeHints.Fixed(new Size(0, 0));

    protected override void RenderOverride(CellBuffer buffer)
    {
        var area = Visible(buffer);
        if (area.Width <= 0 || area.Height <= 0) return;
        var now = _clock();
        if (_deltaPending) StartFades(buffer, area, now);
        foreach (var ((row, col), start) in _fades)
            PaintFade(buffer, area, Bounds.X + col, Bounds.Y + row, Motion.FadeProgress(start, now));
    }

    /// <summary>The body cells this render may read: its bounds inside the current clip.</summary>
    private Rectangle Visible(CellBuffer buffer)
    {
        var clip = XenoCells.Clip(buffer);
        var b = Bounds;
        var x0 = Math.Max(b.X, clip.X);
        var y0 = Math.Max(b.Y, clip.Y);
        var x1 = Math.Min(b.X + b.Width, clip.X + clip.Width);
        var y1 = Math.Min(b.Y + b.Height, clip.Y + clip.Height);
        return new Rectangle(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>Compares the visible cells with the snapshot: a changed, unknown, non-blank text
    /// cell starts fading now. The snapshot then holds what is shown.</summary>
    private void StartFades(CellBuffer buffer, Rectangle area, long now)
    {
        _deltaPending = false;
        var started = false;
        for (var y = area.Y; y < area.Y + area.Height; y++)
        for (var x = area.X; x < area.X + area.Width; x++)
        {
            var cell = XenoCells.Read(buffer, x, y);
            var key = (y - Bounds.Y, x - Bounds.X);
            var changed = !_snapshot.TryGetValue(key, out var before) || !before.SameGlyph(cell);
            _snapshot[key] = cell;
            if (!changed || cell.Blank || cell.Placeholder) continue;
            _fades[key] = now;
            started = true;
        }
        if (!started) return;
        _next = now;
        if (App is { } app) XenoCells.RequestAnimation(app);
    }

    /// <summary>One cell <paramref name="progress"/> of the way from its background to its colour.</summary>
    private static void PaintFade(CellBuffer buffer, Rectangle area, int x, int y, double progress)
    {
        if (progress >= 1 || !area.Contains(x, y)) return;
        var cell = XenoCells.Read(buffer, x, y);
        if (cell.Placeholder || !cell.Style.TryGetForeground(out var text) || !cell.Style.TryGetBackground(out var background)) return;
        buffer.OverlayCellStyle(x, y, Style.None.WithForeground(Motion.Mix(background, text, progress)));
    }
}
