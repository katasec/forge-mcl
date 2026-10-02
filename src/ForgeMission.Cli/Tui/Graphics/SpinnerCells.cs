using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Animation;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 Task 5: a spinning spinner — the placeholder cells of one of its sent frames, then a
// gap column, and every ForgeTheme.SpinnerTurnMs / SpinnerFrames it names the next frame. No image
// is sent per frame. ChatScreen puts one on screen only while it should spin and removes it after;
// attaching registers it with the app's animation clock and detaching unregisters it, so a screen
// with no spinner costs nothing between frames.
internal sealed class SpinnerCells : Visual, IAnimatedVisual
{
    private readonly SpinnerFrames _frames;
    private readonly Style _fill;
    private readonly Func<long> _clock;
    private readonly long _start;
    private int _frame;
    private long _next;

    public SpinnerCells(SpinnerFrames frames, Style fill, Func<long> clock)
    {
        _frames = frames;
        _fill = fill;
        _clock = clock;
        _start = clock();
        _next = _start;
    }

    long IAnimatedVisual.NextAnimationTick => _next;

    bool IAnimatedVisual.AdvanceAnimation(long timestamp)
    {
        _next = Motion.NextSpinnerFrame(_start, timestamp);
        var frame = Motion.SpinnerFrame(timestamp - _start);
        if (frame == _frame) return false;
        _frame = frame;
        return true;
    }

    protected override SizeHints MeasureCore(in LayoutConstraints constraints) =>
        SizeHints.Fixed(new Size(ForgeTheme.SpinnerCols + ForgeTheme.SpinnerGapCols, 1));

    protected override void RenderOverride(CellBuffer buffer)
    {
        var b = Bounds;
        if (b.Height < 1) return;
        var frame = Motion.SpinnerFrame(_clock() - _start);
        var style = _fill.WithForeground(KittyImages.IdColor(_frames.Ids[frame]));
        for (var col = 0; col < b.Width; col++)
        {
            if (col < ForgeTheme.SpinnerCols) buffer.WriteText(b.X + col, b.Y, KittyImages.Cell(0, col), style);
            else buffer.SetCell(b.X + col, b.Y, new System.Text.Rune(' '), _fill);
        }
    }
}
