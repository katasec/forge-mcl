using XenoAtom.Terminal.UI;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 Task 5: a spinner's image frames (the mockup's .dot: a ring in Track with an Arc-coloured
// quarter, spinning once per ForgeTheme.SpinnerTurnMs). Each frame is the ring turned one step
// further, drawn once at start-up on the surface it sits on, ForgeTheme.SpinnerCols columns by one
// row, and sent once with the tiles; SpinnerCells only changes which frame its cells name.
// Ids have bit 22 set (tile ids use bits 0–21, text ids bit 23): bit 22 | theme | cell width |
// cell height | 6-bit index (spinner × frames + frame), so a spinner id never meets another image's.

/// <summary>What a spinner is drawn from: ForgeTheme tokens; lengths in mockup px.</summary>
internal sealed record SpinnerShape(Color Surface, Color Track, Color Arc, double Diameter, double Stroke);

internal sealed class SpinnerFrames
{
    private const uint SpinnerBit = 1u << 22;
    private const int ThemeBits = 1, WidthBits = 7, HeightBits = 8, IndexBits = 6;

    private SpinnerFrames(IReadOnlyList<Tile> frames, IReadOnlyList<uint> ids) => (Frames, Ids) = (frames, ids);

    /// <summary>The frames, in turning order.</summary>
    public IReadOnlyList<Tile> Frames { get; }

    /// <summary>The image id of each frame.</summary>
    public IReadOnlyList<uint> Ids { get; }

    /// <summary>Spinner <paramref name="spinner"/> (0: progress row, 1: tool chip) at the cell size.</summary>
    public static SpinnerFrames Create(SpinnerShape shape, int themeSlot, int spinner, CellSize cell)
    {
        var frames = Enumerable.Range(0, ForgeTheme.SpinnerFrames)
            .Select(frame => new Tile(Draw(shape, cell, frame), ForgeTheme.SpinnerCols, 1))
            .ToList();
        return new SpinnerFrames(frames, ImageIds(themeSlot, spinner, cell.Width, cell.Height));
    }

    /// <summary>Sends every frame. Must run after the TUI has entered the alternate screen.</summary>
    public void Transmit()
    {
        for (var i = 0; i < Frames.Count; i++)
            KittyImages.Transmit(Ids[i], Png.Encode(Frames[i].Image), Frames[i].Cols, Frames[i].Rows);
    }

    /// <summary>The frame ids of one spinner: bit 22 | theme | cell width | cell height | index.</summary>
    internal static uint[] ImageIds(int themeSlot, int spinner, int cellWidth, int cellHeight)
    {
        if (themeSlot is < 0 or >= 1 << ThemeBits || cellWidth is < 1 or >= 1 << WidthBits ||
            cellHeight is < 1 or >= 1 << HeightBits || spinner is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(cellWidth), "Spinner ids need a theme slot 0..1, spinner 0..1 and a cell size inside the id bits.");
        var cellPart = ((uint)themeSlot << WidthBits | (uint)cellWidth) << HeightBits | (uint)cellHeight;
        return [.. Enumerable.Range(0, ForgeTheme.SpinnerFrames)
            .Select(frame => SpinnerBit | cellPart << IndexBits | (uint)(spinner * ForgeTheme.SpinnerFrames + frame))];
    }

    /// <summary>The ring in Track, then the arc in Arc turned <paramref name="frame"/> steps
    /// clockwise, centred in the frame's cells.</summary>
    private static RgbImage Draw(SpinnerShape shape, CellSize cell, int frame)
    {
        var (w, h) = (ForgeTheme.SpinnerCols * cell.Width, cell.Height);
        var px = cell.Height / ForgeTheme.MockupRowPx;
        var (cx, cy, radius, stroke) = (w / 2.0, h / 2.0, shape.Diameter * px / 2, shape.Stroke * px);
        var canvas = new LinearCanvas(w, h, Rgb.From(shape.Surface));
        var ring = Shapes.Ring(w, h, cx, cy, radius, stroke);
        canvas.Composite(ring, Rgb.From(shape.Track), 1);
        var turn = 2 * Math.PI * frame / ForgeTheme.SpinnerFrames;
        var half = ForgeTheme.SpinnerArcDegrees * Math.PI / 360;
        canvas.Composite(Shapes.Arc(ring, w, h, cx, cy, turn, half), Rgb.From(shape.Arc), 1);
        return canvas.ToSrgb();
    }
}
