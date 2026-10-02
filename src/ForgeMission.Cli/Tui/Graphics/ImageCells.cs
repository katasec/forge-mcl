using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 Task 4: the placeholder cells of one sent text image (brand, breadcrumb, a card name, an
// avatar, a key-hint chip, the send button), at its fixed size. The cells carry the image's
// background as their own, so a cell the image does not reach matches it.
internal sealed class ImageCells(TextImage image, Style fill) : Visual
{
    public TextImage Image => image;

    protected override SizeHints MeasureCore(in LayoutConstraints constraints) => SizeHints.Fixed(new Size(image.Cols, image.Rows));

    protected override void RenderOverride(CellBuffer buffer) => Paint(buffer, Bounds, image, 0, fill);

    /// <summary>Paints image rows from <paramref name="top"/> into <paramref name="bounds"/>,
    /// clipped to it; cells past the image's width are plain fill.</summary>
    public static void Paint(CellBuffer buffer, Rectangle bounds, TextImage image, int top, Style fill)
    {
        var style = fill.WithForeground(KittyImages.IdColor(image.Id));
        for (var row = 0; row < image.Rows && row < bounds.Height - top; row++)
        for (var col = 0; col < bounds.Width; col++)
        {
            var (x, y) = (bounds.X + col, bounds.Y + top + row);
            if (col < image.Cols) buffer.WriteText(x, y, KittyImages.Cell(row, col), style);
            else buffer.SetCell(x, y, new System.Text.Rune(' '), fill);
        }
    }
}
