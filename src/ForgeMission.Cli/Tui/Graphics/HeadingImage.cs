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
internal sealed partial class HeadingImage(TextImages images, TextKind kind, string text, bool pending, Style fill, Style pendingText)
    : Visual
{
    private IReadOnlyList<HeadingLine> _lines = [];
    private int _wrapCols = -1;
    private TextImage[] _images = [];
    private int _selectionAnchor = -1;
    private int _selectionActive = -1;
    private int _selectionVersion;

    [Bindable]
    internal partial int InteractionVersion { get; set; }

    internal string Text => text;

    internal bool HasSelection => _selectionAnchor >= 0 && _selectionActive >= 0 && _selectionAnchor != _selectionActive;

    internal int TextIndexAt(int localX, int localY)
    {
        if (text.Length == 0 || _lines.Count == 0) return 0;
        var lineIndex = Math.Clamp(localY / TextArt.HeadingRows, 0, _lines.Count - 1);
        var line = _lines[lineIndex];
        if (line.Text.Length == 0 || Bounds.Width <= 0) return line.SourceStart;
        var fraction = Math.Clamp((double)localX / Bounds.Width, 0, 1);
        var visual = (int)Math.Round(line.Text.Length * fraction);
        return line.SourceIndexAtVisual(visual);
    }

    internal void SetSelection(int anchor, int active)
    {
        _selectionAnchor = Math.Clamp(anchor, 0, text.Length);
        _selectionActive = Math.Clamp(active, 0, text.Length);
        Touch();
    }

    internal void ClearSelection()
    {
        if (_selectionAnchor < 0 && _selectionActive < 0) return;
        _selectionAnchor = _selectionActive = -1;
        Touch();
    }

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
        if (!pending) _images = [.. _lines.Select(line => images.Get(new TextImageRequest(kind, line.Text)))];
    }

    protected override void RenderOverride(CellBuffer buffer)
    {
        _ = InteractionVersion;
        var b = Bounds;
        for (var y = b.Y; y < b.Y + b.Height; y++)
        for (var x = b.X; x < b.X + b.Width; x++)
            buffer.SetCell(x, y, new System.Text.Rune(' '), fill);
        for (var i = 0; i < _lines.Count; i++)
        {
            var top = i * TextArt.HeadingRows;
            if (pending) buffer.WriteText(b.X, b.Y + top, Trim(_lines[i].Text, b.Width), pendingText);
            else if (i < _images.Length) ImageCells.Paint(buffer, b, _images[i], top, fill);
        }
        OverlaySelection(buffer, b);
    }

    private void WrapAt(int cols)
    {
        if (cols == _wrapCols) return;
        _wrapCols = cols;
        _lines = images.WrapHeading(kind, text, cols);
    }

    private void OverlaySelection(CellBuffer buffer, Rectangle bounds)
    {
        if (!HasSelection) return;
        var start = Math.Min(_selectionAnchor, _selectionActive);
        var end = Math.Max(_selectionAnchor, _selectionActive);
        for (var lineIndex = 0; lineIndex < _lines.Count; lineIndex++)
        {
            var line = _lines[lineIndex];
            if (start >= line.SourceEnd || end <= line.SourceStart || line.Text.Length == 0) continue;
            var selectedStart = line.VisualIndexAtSource(Math.Max(start, line.SourceStart));
            var selectedEnd = line.VisualIndexAtSource(Math.Min(end, line.SourceEnd));
            var x0 = bounds.X + selectedStart * bounds.Width / line.Text.Length;
            var x1 = bounds.X + Math.Max(x0 - bounds.X + 1, selectedEnd * bounds.Width / line.Text.Length);
            for (var y = bounds.Y + lineIndex * TextArt.HeadingRows; y < bounds.Y + (lineIndex + 1) * TextArt.HeadingRows; y++)
            for (var x = x0; x < Math.Min(bounds.Right, x1); x++)
                buffer.OverlayCellStyle(x, y, GetTheme().SelectionStyle());
        }
    }

    private void Touch()
    {
        _selectionVersion++;
        InteractionVersion = _selectionVersion;
    }

    /// <summary>A line's terminal text cut to <paramref name="width"/> cells with "…": monospace
    /// text is wider than the proportional line it stands in for.</summary>
    private static string Trim(string line, int width) => line.Length <= width ? line : line[..Math.Max(0, width - 1)] + "…";
}
