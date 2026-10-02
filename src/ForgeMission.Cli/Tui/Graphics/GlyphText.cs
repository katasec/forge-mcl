using StbTrueTypeSharp;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 G2: the one adapter over StbTrueTypeSharp, and the only file that references it or uses
// unsafe code (TuiColourLiteralTests). It loads one embedded TTF and turns a line of text into a
// coverage mask (area coverage per pixel, linear 0..1) with the font's GPOS pair kerning (G9,
// GposKerning) and CSS-style letter spacing. Callers pass only text TextArt.Allows, which the
// embedded subset covers (GlyphTextTests).

/// <summary>A measured line in px: the total advance (kerning and tracking included), and the
/// font's ascent above and descent below the baseline.</summary>
internal readonly record struct LineMetrics(double Advance, double Ascent, double Descent);

/// <summary>The requested font is not an embedded resource.</summary>
internal sealed class FontMissingException(string name, IEnumerable<string> embedded)
    : Exception($"Embedded font '{name}' not found. Embedded resources: [{string.Join(", ", embedded)}].");

internal sealed unsafe class GlyphText
{
    // Pinned for the font's lifetime: stbtt_fontinfo keeps a raw pointer into it.
    private readonly byte[] _font;
    private readonly StbTrueType.stbtt_fontinfo _info = new();
    private readonly GposKerning _kerning;
    private readonly int _ascent, _descent;

    private GlyphText(byte[] font)
    {
        _font = font;
        fixed (byte* data = _font)
        {
            if (StbTrueType.stbtt_InitFont(_info, data, StbTrueType.stbtt_GetFontOffsetForIndex(data, 0)) == 0)
                throw new InvalidDataException("StbTrueType could not parse the embedded font.");
        }
        _kerning = GposKerning.Read(_font);
        (_ascent, _descent) = VerticalMetrics();
    }

    /// <summary>Loads the TTF embedded in forge as <paramref name="name"/>.</summary>
    public static GlyphText LoadEmbedded(string name)
    {
        var assembly = typeof(GlyphText).Assembly;
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new FontMissingException(name, assembly.GetManifestResourceNames());
        var font = GC.AllocateUninitializedArray<byte>((int)stream.Length, pinned: true);
        stream.ReadExactly(font);
        return new GlyphText(font);
    }

    /// <summary>Whether the font has a glyph for <paramref name="codepoint"/>.</summary>
    public bool HasGlyph(int codepoint) => StbTrueType.stbtt_FindGlyphIndex(_info, codepoint) != 0;

    /// <summary>The GPOS kerning between two characters, in font units.</summary>
    public int KernUnits(int left, int right) => _kerning.Adjust(Glyph(left), Glyph(right));

    public LineMetrics Measure(string text, double emPx, double trackingEm)
    {
        var scale = Scale(emPx);
        double x = 0;
        for (var i = 0; i < text.Length; i++) x += Step(text, i, scale, trackingEm * emPx);
        return new LineMetrics(x, _ascent * scale, -_descent * scale);
    }

    /// <summary>Coverage of characters [from, to) of <paramref name="text"/>, laid out as the whole
    /// line from (originX, baseline) on a width x height mask, glyphs at subpixel x positions.</summary>
    public float[] Coverage(string text, int from, int to, double emPx, double trackingEm, int width, int height,
        double originX, double baseline)
    {
        var scale = Scale(emPx);
        var mask = new float[width * height];
        var x = originX;
        for (var i = 0; i < text.Length; i++)
        {
            if (i >= from && i < to) DrawGlyph(mask, width, height, Glyph(text[i]), scale, x, (int)Math.Round(baseline));
            x += Step(text, i, scale, trackingEm * emPx);
        }
        return mask;
    }

    // ── Stb calls ────────────────────────────────────────────────────────────────────────────

    private float Scale(double emPx) => StbTrueType.stbtt_ScaleForMappingEmToPixels(_info, (float)emPx);

    private int Glyph(int codepoint) => StbTrueType.stbtt_FindGlyphIndex(_info, codepoint);

    /// <summary>How far the pen moves after character i: its advance, the tracking, and the
    /// kerning with the next character.</summary>
    private double Step(string text, int i, float scale, double trackingPx)
    {
        int advance, bearing;
        StbTrueType.stbtt_GetGlyphHMetrics(_info, Glyph(text[i]), &advance, &bearing);
        var kern = i + 1 < text.Length ? KernUnits(text[i], text[i + 1]) : 0;
        return (advance + kern) * scale + trackingPx;
    }

    private (int Ascent, int Descent) VerticalMetrics()
    {
        int ascent, descent, gap;
        StbTrueType.stbtt_GetFontVMetrics(_info, &ascent, &descent, &gap);
        return (ascent, descent);
    }

    private void DrawGlyph(float[] mask, int width, int height, int glyph, float scale, double x, int baseline)
    {
        var shift = (float)(x - Math.Floor(x));
        int x0, y0, x1, y1;
        StbTrueType.stbtt_GetGlyphBitmapBoxSubpixel(_info, glyph, scale, scale, shift, 0, &x0, &y0, &x1, &y1);
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return;
        var bitmap = new byte[w * h];
        fixed (byte* g = bitmap)
            StbTrueType.stbtt_MakeGlyphBitmapSubpixel(_info, g, w, h, w, scale, scale, shift, 0, glyph);
        AddCoverage(mask, width, height, bitmap, w, h, (int)Math.Floor(x) + x0, baseline + y0);
    }

    private static void AddCoverage(float[] mask, int width, int height, byte[] glyph, int w, int h, int ox, int oy)
    {
        for (var gy = 0; gy < h; gy++)
        for (var gx = 0; gx < w; gx++)
        {
            int px = ox + gx, py = oy + gy;
            if (px < 0 || py < 0 || px >= width || py >= height) continue;
            var i = py * width + px;
            mask[i] = Math.Min(1f, mask[i] + glyph[gy * w + gx] / 255f);
        }
    }
}
