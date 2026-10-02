using XenoAtom.Terminal.UI;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 Task 4: draws one proportional-text image — the brand, breadcrumb, a card name, one line
// of a heading, an avatar, a key-hint chip or the send button — at the cell size, in whole cells.
// Lengths arrive in mockup px (1 mockup px = cell height / MockupRowPx); colours as ForgeTheme
// tokens (TextArtStyle). Shapes are drawn in linear light like every tile; text is then blended on
// the surface below it as the theme's TextBlend says. Every line box is centred in its rows the
// way CSS centres one (the font's ascent plus descent). Image text is simple Latin only (G9):
// callers check Allows first and keep anything else as terminal text.

/// <summary>How text coverage is blended: on the sRGB values (light theme) or in linear light (dark).</summary>
internal enum TextBlend { Srgb, Linear }

/// <summary>Which image: each kind has its look in <see cref="TextArtStyle"/>.</summary>
internal enum TextKind { Brand, Crumb, Name, Heading1, Heading2, Heading3, Avatar, UserAvatar, Chip, Send }

/// <summary>One text image: its kind and text. A breadcrumb's text from <paramref name="Split"/> on
/// is drawn strong; every other kind ignores it.</summary>
internal readonly record struct TextImageRequest(TextKind Kind, string Text, int Split = 0);

/// <summary>A line of text in one face: SemiBold, or Bold when <paramref name="Bold"/>; size and
/// tracking in mockup px and em, on <paramref name="Background"/>.</summary>
internal sealed record LineLook(bool Bold, double Size, double TrackingEm, Color Text, Color Background);

/// <summary>A filled rounded square (a circle when the radius is half the size) with its glyphs
/// centred in it; <see cref="LineLook.Background"/> is the surface around the shape.</summary>
internal sealed record ShapeLook(double Size, double Radius, Color Fill, LineLook Glyph);

/// <summary>A key-hint chip: a rounded box, its border a hairline with a thicker bottom edge,
/// the text inside with <paramref name="PadX"/> on each side.</summary>
internal sealed record ChipLook(LineLook Text, double PadX, double Radius, double Hairline, double BottomHairline,
    Color Fill, Color Border);

/// <summary>Every look TextArt draws, built from one ForgeTheme by ForgeStyles.</summary>
internal sealed record TextArtStyle(
    TextBlend Blend,
    LineLook Brand, ShapeLook Logo, double LogoGap, string LogoGlyph,
    LineLook Crumb, Color CrumbStrong,
    LineLook Name,
    LineLook Heading1, LineLook Heading2, LineLook Heading3,
    ShapeLook Avatar, ShapeLook UserAvatar,
    ChipLook Chip,
    ShapeLook Send);

/// <summary>The two embedded Inter weights.</summary>
internal sealed record TextFonts(GlyphText SemiBold, GlyphText Bold)
{
    /// <summary>Loads both weights; a missing or unreadable font throws (FontMissingException,
    /// InvalidDataException) so forge chat stops before the TUI starts.</summary>
    public static TextFonts LoadEmbedded() => new(GlyphText.LoadEmbedded("Inter-SemiBold.ttf"), GlyphText.LoadEmbedded("Inter-Bold.ttf"));

    public GlyphText For(LineLook look) => look.Bold ? Bold : SemiBold;
}

/// <summary>A drawn image and the cells it covers.</summary>
internal sealed record TextArtImage(RgbImage Image, int Cols, int Rows);

internal sealed class TextArt(TextArtStyle style, TextFonts fonts, CellSize cell)
{
    /// <summary>Rows a heading line covers (its CSS line box, two rows).</summary>
    public const int HeadingRows = 2;

    // Space at the left and right of every image, in device px: no glyph or shape pixel is clipped,
    // and the edge columns are the plain surface the cells beside the image carry.
    private const double EdgePx = 1;

    /// <summary>Whether <paramref name="text"/> may be drawn as an image (G9): non-empty, and
    /// printable ASCII, Latin-1 letters and · … → ↵ ⇧ – — ‘ ’ “ ” only. The embedded subset holds
    /// exactly these (eng/fonts/inter-subset.sh).</summary>
    public static bool Allows(string text) => text.Length > 0 && text.All(Allowed);

    private const string AllowedSymbols = "·…→↵⇧–—‘’“”";

    private static bool Allowed(char c) =>
        c is >= ' ' and <= '~' || (c is >= 'À' and <= 'ÿ' && c is not ('×' or '÷')) || AllowedSymbols.Contains(c);

    public TextArtImage Render(TextImageRequest request) => request.Kind switch
    {
        TextKind.Brand => Brand(request.Text),
        TextKind.Crumb => Line(request.Text, style.Crumb, request.Split, style.CrumbStrong, 1),
        TextKind.Name => Line(request.Text, style.Name, request.Text.Length, style.Name.Text, 1),
        TextKind.Heading1 or TextKind.Heading2 or TextKind.Heading3 =>
            Line(request.Text, HeadingLook(request.Kind), request.Text.Length, HeadingLook(request.Kind).Text, HeadingRows),
        TextKind.Avatar => Badge(request.Text, style.Avatar, centred: false),
        TextKind.UserAvatar => Badge(request.Text, style.UserAvatar, centred: false),
        TextKind.Chip => Chip(request.Text),
        TextKind.Send => Badge(request.Text, style.Send, centred: true),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "No look for this kind."),
    };

    /// <summary>Splits a heading into lines that each fit <paramref name="maxCols"/> cells, at word
    /// breaks; a word wider than a line on its own is cut to fit and ends in "…".</summary>
    public IReadOnlyList<string> Wrap(TextKind kind, string text, int maxCols)
    {
        var look = HeadingLook(kind);
        var maxPx = maxCols * cell.Width - 2 * EdgePx;
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var joined = line.Length == 0 ? word : $"{line} {word}";
            if (Fits(joined, look, maxPx)) { line = joined; continue; }
            if (line.Length > 0) lines.Add(line);
            line = Fits(word, look, maxPx) ? word : Cut(word, look, maxPx);
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    /// <summary>The look of a heading level.</summary>
    public LineLook HeadingLook(TextKind kind) => kind switch
    {
        TextKind.Heading1 => style.Heading1,
        TextKind.Heading2 => style.Heading2,
        TextKind.Heading3 => style.Heading3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a heading."),
    };

    // ── Kinds ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Text on its surface, as wide as it needs; from <paramref name="split"/> on in
    /// <paramref name="strong"/>.</summary>
    private TextArtImage Line(string text, LineLook look, int split, Color strong, int rows)
    {
        var font = fonts.For(look);
        var em = Px(look.Size);
        var metrics = font.Measure(text, em, look.TrackingEm);
        var cols = Cols(EdgePx + metrics.Advance + EdgePx);
        var (w, h) = (cols * cell.Width, rows * cell.Height);
        var image = new LinearCanvas(w, h, Rgb.From(look.Background)).ToSrgb();
        var baseline = Baseline(metrics, 0, h);
        Blend(image, font.Coverage(text, 0, split, em, look.TrackingEm, w, h, EdgePx, baseline), look.Text);
        if (split < text.Length) Blend(image, font.Coverage(text, split, text.Length, em, look.TrackingEm, w, h, EdgePx, baseline), strong);
        return new TextArtImage(image, cols, rows);
    }

    /// <summary>The logo square with its glyph, the gap, then the word.</summary>
    private TextArtImage Brand(string word)
    {
        var look = style.Brand;
        var font = fonts.For(look);
        var em = Px(look.Size);
        var metrics = font.Measure(word, em, look.TrackingEm);
        var (logo, gap) = (Px(style.Logo.Size), Px(style.LogoGap));
        var cols = Cols(EdgePx + logo + gap + metrics.Advance + EdgePx);
        var (w, h) = (cols * cell.Width, cell.Height);
        var top = (h - logo) / 2;
        var image = Filled(w, h, look.Background, style.Logo.Fill, new RectF(EdgePx, top, EdgePx + logo, top + logo), Px(style.Logo.Radius));
        Centred(image, style.LogoGlyph, style.Logo.Glyph, EdgePx, top, logo, logo);
        Blend(image, font.Coverage(word, 0, word.Length, em, look.TrackingEm, w, h, EdgePx + logo + gap, Baseline(metrics, 0, h)), look.Text);
        return new TextArtImage(image, cols, 1);
    }

    /// <summary>A filled shape one row tall at most, its glyphs centred in it; left in its cells
    /// (an avatar lines up with the text column) or centred (the send button).</summary>
    private TextArtImage Badge(string text, ShapeLook shape, bool centred)
    {
        var size = Math.Min(Px(shape.Size), cell.Height);
        var cols = Cols(EdgePx + size + EdgePx);
        var (w, h) = (cols * cell.Width, cell.Height);
        var (left, top) = (centred ? (w - size) / 2 : EdgePx, (h - size) / 2);
        var image = Filled(w, h, shape.Glyph.Background, shape.Fill, new RectF(left, top, left + size, top + size),
            Math.Min(Px(shape.Radius), size / 2));
        if (text.Length > 0) Centred(image, text, shape.Glyph, left, top, size, size);
        return new TextArtImage(image, cols, 1);
    }

    /// <summary>The chip: border box, fill box inset by the hairlines, the text centred inside.</summary>
    private TextArtImage Chip(string text)
    {
        var chip = style.Chip;
        var font = fonts.For(chip.Text);
        var metrics = font.Measure(text, Px(chip.Text.Size), chip.Text.TrackingEm);
        var (hair, bottom) = (Px(chip.Hairline), Px(chip.BottomHairline));
        var boxW = metrics.Advance + 2 * Px(chip.PadX) + 2 * hair;
        var boxH = Math.Min(metrics.Ascent + metrics.Descent + 2 * Px(1) + hair + bottom, cell.Height);
        var cols = Cols(EdgePx + boxW + EdgePx);
        var (w, h) = (cols * cell.Width, cell.Height);
        var top = (h - boxH) / 2;
        var outer = new RectF(EdgePx, top, EdgePx + boxW, top + boxH);
        var inner = new RectF(EdgePx + hair, top + hair, EdgePx + boxW - hair, top + boxH - bottom);
        var canvas = new LinearCanvas(w, h, Rgb.From(chip.Text.Background));
        canvas.Composite(Shapes.RoundedRect(w, h, outer, Px(chip.Radius)), Rgb.From(chip.Border), 1);
        canvas.Composite(Shapes.RoundedRect(w, h, inner, Math.Max(0, Px(chip.Radius) - hair)), Rgb.From(chip.Fill), 1);
        var image = canvas.ToSrgb();
        Centred(image, text, chip.Text, inner.X0, inner.Y0, inner.X1 - inner.X0, inner.Y1 - inner.Y0);
        return new TextArtImage(image, cols, 1);
    }

    // ── Steps ───────────────────────────────────────────────────────────────────────────────

    private static RgbImage Filled(int w, int h, Color surface, Color fill, RectF shape, double radius)
    {
        var canvas = new LinearCanvas(w, h, Rgb.From(surface));
        canvas.Composite(Shapes.RoundedRect(w, h, shape, radius), Rgb.From(fill), 1);
        return canvas.ToSrgb();
    }

    /// <summary>Draws <paramref name="text"/> centred in the box (x, y, w, h).</summary>
    private void Centred(RgbImage image, string text, LineLook look, double x, double y, double w, double h)
    {
        var font = fonts.For(look);
        var em = Px(look.Size);
        var metrics = font.Measure(text, em, look.TrackingEm);
        var coverage = font.Coverage(text, 0, text.Length, em, look.TrackingEm, image.Width, image.Height,
            x + (w - metrics.Advance) / 2, Baseline(metrics, y, h));
        Blend(image, coverage, look.Text);
    }

    /// <summary>The baseline that centres the line box (ascent plus descent) in a band, as CSS does.</summary>
    private static double Baseline(LineMetrics metrics, double top, double height) =>
        top + (height - (metrics.Ascent + metrics.Descent)) / 2 + metrics.Ascent;

    private void Blend(RgbImage image, float[] coverage, Color text) => Srgb.BlendText(image, coverage, Rgb.From(text), style.Blend);

    private bool Fits(string text, LineLook look, double maxPx) =>
        fonts.For(look).Measure(text, Px(look.Size), look.TrackingEm).Advance <= maxPx;

    /// <summary>The longest start of <paramref name="word"/> that fits with "…" after it.</summary>
    private string Cut(string word, LineLook look, double maxPx)
    {
        for (var length = word.Length - 1; length > 0; length--)
        {
            var cut = word[..length] + "…";
            if (Fits(cut, look, maxPx)) return cut;
        }
        return "…";
    }

    private double Px(double mockupPx) => mockupPx * cell.Height / ForgeTheme.MockupRowPx;

    private int Cols(double px) => Math.Max(1, (int)Math.Ceiling(px / cell.Width));
}
