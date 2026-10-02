using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Animation;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;

namespace ForgeMission.Cli.Tui;

// Phase 56 Task 5 (.caret): while a reply is pending or streams, an Accent block blinks one cell
// after its last text cell (on for ForgeTheme.CaretBlinkMs, off as long), and goes when the reply
// ends. An overlay drawn after the body and its fade, so it is never faded; it is not part of the
// Markdown, so headings and code blocks parse as they would without it. The last text cell is read
// from the rendered body (XenoCells): the last row holding a non-blank cell that is not an image
// placeholder. A pending body has no text, so the caret sits at its first cell and the overlay
// asks for that one row. Inactive, it draws nothing and asks for no frames.
internal sealed class StreamCaret : Visual, IAnimatedVisual
{
    private readonly Style _style;
    private readonly Func<long> _clock;
    private readonly State<bool> _active = new(false);
    private long _start;
    private bool _on;
    private long _next = long.MaxValue;

    public StreamCaret(Style style, Func<long> clock)
    {
        _style = style;
        _clock = clock;
        IsHitTestVisible = false;
        HorizontalAlignment = Align.Stretch;
        VerticalAlignment = Align.Stretch;
    }

    /// <summary>Whether the caret shows (the reply is pending or streams). Turning it on restarts
    /// the blink with the caret visible.</summary>
    public bool Active
    {
        get => _active.Value;
        set
        {
            if (_active.Value == value) return;
            _active.Value = value;
            _start = _clock();
            _on = value;
            _next = value ? Motion.NextCaretToggle(_start, _start) : long.MaxValue;
            if (value && App is { } app) XenoCells.RequestAnimation(app);
        }
    }

    long IAnimatedVisual.NextAnimationTick => _next;

    bool IAnimatedVisual.AdvanceAnimation(long timestamp)
    {
        if (!_active.Value)
        {
            _next = long.MaxValue;
            return false;
        }
        _next = Motion.NextCaretToggle(_start, timestamp);
        var on = Motion.CaretOn(timestamp - _start);
        if (on == _on) return false;
        _on = on;
        return true;
    }

    /// <summary>One row while active, so a pending (empty) body still has a cell for the caret.</summary>
    protected override SizeHints MeasureCore(in LayoutConstraints constraints) =>
        SizeHints.Fixed(new Size(0, _active.Value ? 1 : 0));

    protected override void RenderOverride(CellBuffer buffer)
    {
        if (!_active.Value || !Motion.CaretOn(_clock() - _start)) return;
        if (CaretCell(buffer) is not { } at) return;
        buffer.WriteText(at.X, at.Y, ForgeTheme.CaretGlyph, _style);
    }

    /// <summary>The cell after the body's last text cell, if it is visible and inside the body.</summary>
    private (int X, int Y)? CaretCell(CellBuffer buffer)
    {
        var clip = XenoCells.Clip(buffer);
        var b = Bounds;
        var (x0, x1) = (Math.Max(b.X, clip.X), Math.Min(b.X + b.Width, clip.X + clip.Width));
        var (y0, y1) = (Math.Max(b.Y, clip.Y), Math.Min(b.Y + b.Height, clip.Y + clip.Height));
        for (var y = y1 - 1; y >= y0; y--)
        {
            var last = LastTextColumn(buffer, y, x0, x1);
            if (last >= 0) return last + 1 < x1 ? (last + 1, y) : null;
        }
        // No text visible: a pending body, or text scrolled away above.
        return y0 == b.Y && y0 < y1 && x0 < x1 ? (x0, y0) : null;
    }

    private static int LastTextColumn(CellBuffer buffer, int y, int x0, int x1)
    {
        for (var x = x1 - 1; x >= x0; x--)
        {
            var cell = XenoCells.Read(buffer, x, y);
            if (!cell.Blank && !cell.Placeholder) return x;
        }
        return -1;
    }
}
