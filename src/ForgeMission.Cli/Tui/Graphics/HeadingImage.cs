using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 Task 4: a Markdown heading (h1–h3) in Inter, one image per wrapped line, each line two
// rows (its CSS line box). While the heading streams (pending) the same lines show as bold terminal
// text on the first row of each pair, so the image takes exactly the rows the text held and nothing
// below it moves. Lines wrap at the width the heading is arranged at; a one-line heading keeps its
// image across window resizes, a wrapped one gets new line images (accepted). Images are asked for
// in Arrange, the first point that knows the final width, on the UI thread before the frame is
// written; Measure only counts lines.
internal sealed class HeadingImage(TextImages images, TextKind kind, string text, bool pending, Style fill, Style pendingText)
    : Visual
{
    private IReadOnlyList<string> _lines = [];
    private int _wrapCols = -1;
    private TextImage[] _images = [];

    protected override SizeHints MeasureCore(in LayoutConstraints constraints)
    {
        var cols = constraints.MaxWidth == int.MaxValue ? PlaceholderDiacritics.Values.Length : Math.Max(1, constraints.MaxWidth);
        WrapAt(cols);
        return SizeHints.Fixed(new Size(cols, _lines.Count * TextArt.HeadingRows));
    }

    protected override void ArrangeCore(in Rectangle finalRect)
    {
        base.ArrangeCore(finalRect);
        WrapAt(Math.Max(1, finalRect.Width));
        if (!pending) _images = [.. _lines.Select(line => images.Get(new TextImageRequest(kind, line)))];
    }

    protected override void RenderOverride(CellBuffer buffer)
    {
        var b = Bounds;
        for (var y = b.Y; y < b.Y + b.Height; y++)
        for (var x = b.X; x < b.X + b.Width; x++)
            buffer.SetCell(x, y, new System.Text.Rune(' '), fill);
        for (var i = 0; i < _lines.Count; i++)
        {
            var top = i * TextArt.HeadingRows;
            if (pending) buffer.WriteText(b.X, b.Y + top, Trim(_lines[i], b.Width), pendingText);
            else if (i < _images.Length) ImageCells.Paint(buffer, b, _images[i], top, fill);
        }
    }

    private void WrapAt(int cols)
    {
        if (cols == _wrapCols) return;
        _wrapCols = cols;
        _lines = images.WrapHeading(kind, text, cols);
    }

    /// <summary>A line's terminal text cut to <paramref name="width"/> cells with "…": monospace
    /// text is wider than the proportional line it stands in for.</summary>
    private static string Trim(string line, int width) => line.Length <= width ? line : line[..Math.Max(0, width - 1)] + "…";
}
