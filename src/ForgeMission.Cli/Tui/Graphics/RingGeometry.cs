using XenoAtom.Terminal.UI;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56: the card edge ring in device px, from the theme's card tokens and the measured cell
// size. u = cell height / ForgeTheme.MockupRowPx (device px per mockup px). The fit ring keeps the
// mockup's radius and shadow and is as many whole cells thick as the shadow's fade-out plus the
// arc need on each side. That the tiles cut from it are seamless, fade into the surface at the
// ring's edge and leave a plain interior is proven by CardEdgeTests over every realistic cell
// size, not checked at runtime.

/// <summary>What the edge tiles are drawn from: ForgeTheme tokens, lengths in mockup px.</summary>
internal sealed record CardEdges(Color Surface, Color CardSurface, Color CardBorder, double Radius, double Hairline,
    IReadOnlyList<CardShadow> Shadows);

/// <summary>A shadow layer in device px; Sigma is the Gaussian standard deviation (CSS blur / 2).</summary>
internal readonly record struct DeviceShadow(double Dy, double Sigma, Rgb Color, double Alpha);

/// <summary>The solved ring. Insets are px from the ring's outer edge to the border's outer line;
/// Pad* are plain card cells between the ring and the text.</summary>
internal sealed record RingLayout(
    CellSize Cell, int SideCols, int TopRows, int BottomRows,
    double Radius, int Hairline, DeviceShadow[] Shadows,
    int SideInset, int TopInset, int BottomInset,
    int PadCols, int PadTop, int PadBottom);

internal static class RingGeometry
{
    private const int MaxShadowPx = 400;

    /// <summary>Largest output-level difference treated as invisible (the shadow's fade-out).</summary>
    private const double InvisibleLevels = 0.5;

    /// <summary>The fit ring for <paramref name="cell"/>: on each side, the fewest whole cells that
    /// hold the shadow's fade-out plus the corner arc.</summary>
    public static RingLayout Solve(CardEdges edges, CellSize cell)
    {
        var u = cell.Height / ForgeTheme.MockupRowPx;
        var hairline = Math.Max(1, (int)Math.Round(edges.Hairline * u));
        var radius = Math.Max(hairline, edges.Radius * u);
        var shadows = edges.Shadows.Select(s => new DeviceShadow(s.OffsetY * u, s.Blur * u / 2, Rgb.From(s.Color), s.Alpha)).ToArray();
        var surface = Rgb.From(edges.Surface);
        var side = ShadowExtent(surface, shadows, _ => 0);
        var topInset = ShadowExtent(surface, shadows, s => s.Dy);
        var bottomInset = ShadowExtent(surface, shadows, s => -s.Dy);
        var cols = CellsFor(side + radius, cell.Width);
        var top = CellsFor(topInset + radius, cell.Height);
        var bottom = CellsFor(bottomInset + radius, cell.Height);
        return new RingLayout(cell, cols, top, bottom, radius, hairline, shadows, side, topInset, bottomInset,
            PadCols: Math.Max(0, side / cell.Width + ForgeTheme.CardPaddingCols - cols),
            PadTop: Math.Max(0, topInset / cell.Height + ForgeTheme.CardPaddingRows - top),
            PadBottom: Math.Max(0, bottomInset / cell.Height + ForgeTheme.CardPaddingRows - bottom));
    }

    private static int CellsFor(double px, int cellPx) => Math.Max(1, (int)Math.Ceiling(px / cellPx));

    /// <summary>Px beyond the border, on one side, after which the shadow changes the surface by
    /// less than <see cref="InvisibleLevels"/>. <paramref name="towardCard"/> is the layer offset
    /// pointing back at the card on that side (it shortens the shadow there).</summary>
    private static int ShadowExtent(Rgb surfaceColor, DeviceShadow[] shadows, Func<DeviceShadow, double> towardCard)
    {
        var surface = Srgb.ToLinear(surfaceColor);
        for (var d = 0; d < MaxShadowPx; d++)
        {
            var (r, g, b) = surface;
            foreach (var s in shadows)
            {
                var a = (float)(s.Alpha * EdgeCoverage(d + 0.5 + towardCard(s), s.Sigma));
                var (cr, cg, cb) = Srgb.ToLinear(s.Color);
                (r, g, b) = (r + (cr - r) * a, g + (cg - g) * a, b + (cb - b) * a);
            }
            if (Differs(surface, (r, g, b)) < InvisibleLevels) return d;
        }
        return MaxShadowPx;
    }

    /// <summary>Coverage of a blurred half-plane at distance <paramref name="x"/> outside its edge.</summary>
    private static double EdgeCoverage(double x, double sigma) =>
        sigma < 1e-6 ? (x < 0 ? 1 : 0) : 0.5 * (1 - Erf(x / (sigma * Math.Sqrt(2))));

    private static double Differs((float R, float G, float B) a, (float R, float G, float B) b) =>
        Math.Max(Math.Abs(Srgb.Levels(a.R) - Srgb.Levels(b.R)),
            Math.Max(Math.Abs(Srgb.Levels(a.G) - Srgb.Levels(b.G)), Math.Abs(Srgb.Levels(a.B) - Srgb.Levels(b.B))));

    /// <summary>Abramowitz and Stegun 7.1.26 (error below 1.5e-7).</summary>
    private static double Erf(double x)
    {
        var sign = Math.Sign(x);
        x = Math.Abs(x);
        var t = 1 / (1 + 0.3275911 * x);
        var y = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return sign * y;
    }
}
