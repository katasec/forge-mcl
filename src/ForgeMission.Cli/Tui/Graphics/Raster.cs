using XenoAtom.Terminal.UI;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 (G1): pure-C# shape rendering for the card edge tiles. Coverage masks for rounded
// rectangles (signed distance, anti-aliased), Gaussian blur as three box passes, and compositing
// in linear light onto an opaque canvas, and (Task 4) text coverage blended onto a finished image in
// either sRGB or linear light. Every colour arrives as a ForgeTheme token.

/// <summary>An opaque 8-bit sRGB colour, made only from a theme token.</summary>
internal readonly record struct Rgb
{
    private Rgb(byte r, byte g, byte b) => (R, G, B) = (r, g, b);

    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public static Rgb From(Color token) => new(token.R, token.G, token.B);
}

/// <summary>A rectangle in device px (float edges).</summary>
internal readonly record struct RectF(double X0, double Y0, double X1, double Y1)
{
    public RectF Offset(double dx, double dy) => new(X0 + dx, Y0 + dy, X1 + dx, Y1 + dy);

    public RectF Inset(double d) => new(X0 + d, Y0 + d, X1 - d, Y1 - d);
}

/// <summary>An opaque image held in linear light, RGB floats 0..1.</summary>
internal sealed class LinearCanvas
{
    private readonly float[] _rgb;

    public LinearCanvas(int width, int height, Rgb fill)
    {
        Width = width;
        Height = height;
        _rgb = new float[width * height * 3];
        var (r, g, b) = Srgb.ToLinear(fill);
        for (var i = 0; i < _rgb.Length; i += 3) (_rgb[i], _rgb[i + 1], _rgb[i + 2]) = (r, g, b);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Paints <paramref name="color"/> at alpha × coverage, blended in linear light.</summary>
    public void Composite(float[] coverage, Rgb color, double alpha)
    {
        var (r, g, b) = Srgb.ToLinear(color);
        for (var p = 0; p < coverage.Length; p++)
        {
            var a = (float)(coverage[p] * alpha);
            if (a <= 0) continue;
            var i = p * 3;
            _rgb[i] += (r - _rgb[i]) * a;
            _rgb[i + 1] += (g - _rgb[i + 1]) * a;
            _rgb[i + 2] += (b - _rgb[i + 2]) * a;
        }
    }

    public RgbImage ToSrgb()
    {
        var image = RgbImage.Blank(Width, Height);
        for (var i = 0; i < _rgb.Length; i++) image.Pixels[i] = Srgb.FromLinear(_rgb[i]);
        return image;
    }
}

/// <summary>Coverage masks: one float per pixel, 0..1.</summary>
internal static class Shapes
{
    /// <summary>Area coverage of a rounded rectangle, from its signed distance at pixel centres.</summary>
    public static float[] RoundedRect(int width, int height, RectF rect, double radius)
    {
        var mask = new float[width * height];
        double cx = (rect.X0 + rect.X1) / 2, cy = (rect.Y0 + rect.Y1) / 2;
        double hx = (rect.X1 - rect.X0) / 2 - radius, hy = (rect.Y1 - rect.Y0) / 2 - radius;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var qx = Math.Abs(x + 0.5 - cx) - hx;
            var qy = Math.Abs(y + 0.5 - cy) - hy;
            var outside = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2));
            var distance = outside + Math.Min(Math.Max(qx, qy), 0) - radius;
            mask[y * width + x] = (float)Math.Clamp(0.5 - distance, 0, 1);
        }
        return mask;
    }

    /// <summary>Area coverage of a ring centred at (<paramref name="cx"/>, <paramref name="cy"/>):
    /// outer radius <paramref name="radius"/>, <paramref name="stroke"/> px wide (Task 5 spinner).</summary>
    public static float[] Ring(int width, int height, double cx, double cy, double radius, double stroke)
    {
        var mask = new float[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var d = Math.Sqrt(Math.Pow(x + 0.5 - cx, 2) + Math.Pow(y + 0.5 - cy, 2));
            var outer = Math.Clamp(radius - d + 0.5, 0, 1);
            var inner = Math.Clamp(d - (radius - stroke) + 0.5, 0, 1);
            mask[y * width + x] = (float)(outer * inner);
        }
        return mask;
    }

    /// <summary>The part of <paramref name="ring"/> within <paramref name="halfAngle"/> radians of
    /// <paramref name="angle"/> (0 points up, positive turns clockwise), anti-aliased at both ends.</summary>
    public static float[] Arc(float[] ring, int width, int height, double cx, double cy, double angle, double halfAngle)
    {
        var mask = new float[ring.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var (dx, dy) = (x + 0.5 - cx, y + 0.5 - cy);
            var off = Math.Abs(Math.IEEERemainder(Math.Atan2(dx, -dy) - angle, 2 * Math.PI)) - halfAngle;
            var beyond = off >= Math.PI / 2 ? double.MaxValue : Math.Sqrt(dx * dx + dy * dy) * Math.Sin(off);
            mask[y * width + x] = ring[y * width + x] * (float)Math.Clamp(0.5 - beyond, 0, 1);
        }
        return mask;
    }

    /// <summary>Gaussian blur (standard deviation <paramref name="sigma"/> px) as three box blurs
    /// per axis; pixels outside the mask count as zero.</summary>
    public static void Blur(float[] mask, int width, int height, double sigma)
    {
        foreach (var size in BoxSizes(sigma))
        {
            var radius = (size - 1) / 2;
            if (radius == 0) continue;
            BoxPass(mask, width, height, radius, horizontal: true);
            BoxPass(mask, width, height, radius, horizontal: false);
        }
    }

    /// <summary>Box widths whose three passes approximate a Gaussian of <paramref name="sigma"/>.</summary>
    private static int[] BoxSizes(double sigma)
    {
        const int passes = 3;
        var ideal = Math.Sqrt(12 * sigma * sigma / passes + 1);
        var lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0) lower--;
        var upper = lower + 2;
        var lowerCount = (int)Math.Round((12 * sigma * sigma - passes * lower * lower - 4 * passes * lower - 3 * passes) / (-4.0 * lower - 4));
        return [.. Enumerable.Range(0, passes).Select(i => i < lowerCount ? lower : upper)];
    }

    /// <summary>One box blur of every row (or column) of the mask, in place.</summary>
    private static void BoxPass(float[] mask, int width, int height, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width, length = horizontal ? width : height;
        var line = new float[length];
        for (var l = 0; l < lines; l++)
        {
            int start = horizontal ? l * width : l, step = horizontal ? 1 : width;
            for (var i = 0; i < length; i++) line[i] = mask[start + i * step];
            BoxLine(line, mask, start, step, radius);
        }
    }

    /// <summary>Writes the running box average of <paramref name="line"/> back into the mask.</summary>
    private static void BoxLine(float[] line, float[] mask, int start, int step, int radius)
    {
        var scale = 1f / (2 * radius + 1);
        var sum = 0f;
        for (var i = 0; i <= Math.Min(radius, line.Length - 1); i++) sum += line[i];
        for (var i = 0; i < line.Length; i++)
        {
            mask[start + i * step] = sum * scale;
            if (i + radius + 1 < line.Length) sum += line[i + radius + 1];
            if (i - radius >= 0) sum -= line[i - radius];
        }
    }
}

/// <summary>sRGB transfer function, both directions.</summary>
internal static class Srgb
{
    private static readonly float[] Linear = [.. Enumerable.Range(0, 256).Select(v => (float)Decode(v / 255.0))];

    public static (float R, float G, float B) ToLinear(Rgb c) => (Linear[c.R], Linear[c.G], Linear[c.B]);

    public static byte FromLinear(float v) => (byte)Math.Round(Levels(v));

    /// <summary>Paints <paramref name="color"/> at <paramref name="coverage"/> onto <paramref name="image"/>
    /// (Phase 56 Task 4, text): blended on the sRGB values (naive, heavier: dark text on light) or
    /// in linear light (light text on dark), as the theme's TextBlend says.</summary>
    public static void BlendText(RgbImage image, float[] coverage, Rgb color, TextBlend blend)
    {
        byte[] target = [color.R, color.G, color.B];
        for (var p = 0; p < coverage.Length; p++)
        {
            var a = coverage[p];
            if (a <= 0) continue;
            for (var c = 0; c < 3; c++)
            {
                var i = p * 3 + c;
                image.Pixels[i] = blend == TextBlend.Srgb
                    ? (byte)Math.Round(image.Pixels[i] + (target[c] - image.Pixels[i]) * a)
                    : FromLinear(Linear[image.Pixels[i]] + (Linear[target[c]] - Linear[image.Pixels[i]]) * a);
            }
        }
    }

    /// <summary>The sRGB value of linear <paramref name="v"/> in 0..255 levels, unrounded.</summary>
    public static double Levels(double v) => Math.Clamp(Encode(Math.Clamp(v, 0, 1)) * 255, 0, 255);

    private static double Decode(double s) => s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);

    private static double Encode(double l) => l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
}
