using XenoAtom.Terminal.UI;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (53.6): a theme is data — purpose-named colour tokens and shape values.
// This file is the only place a colour literal appears; ForgeStyles builds every component style
// from a ForgeTheme. A new theme is one more instance here plus its name in ForgeConfig.
// Light is sampled from docs/design/forge_tui_light_mockup.png; Dark is the 53.5 mockup's palette.
// Shapes (Phase 56: cards, code blocks, user messages, pills, composer) come from
// docs/design/forge_tui_finish_line_mockup.html (:root and [data-theme="dark"]): lengths in mockup
// px, where 1 mockup px = cell height / MockupRowPx.
internal sealed record ForgeTheme
{
    // ── Layout shared by every theme (finish-line mockup) ───────────────────────────────────

    /// <summary>--cell-h: 20px. One terminal row in the mockup; defines the mockup px.</summary>
    public const double MockupRowPx = 20;

    /// <summary>.scroll padding: … 4ch. The transcript gutter: a card's border lands in this column.</summary>
    public const int TranscriptGutterCols = 4;

    /// <summary>.card padding: var(--cell-h) 3ch. Text sits 3 columns and 1 row inside the border.</summary>
    public const int CardPaddingCols = 3;
    public const int CardPaddingRows = 1;

    /// <summary>.card margin: var(--cell-h) 0. One blank row above each card (the nearest whole row).</summary>
    public const int CardGapRows = 1;

    /// <summary>pre.code padding: … 2ch. Code sits 2 columns inside the code block's outer edge.</summary>
    public const int CodeBlockPaddingCols = 2;

    /// <summary>.you .bubble padding: … 1.6ch. A multi-line user message's text sits 2 columns in.</summary>
    public const int UserPaddingCols = 2;

    /// <summary>A user message (with its label) is at most this share of the transcript wide and
    /// wraps inside it (supervisor ruling, Phase 56 Task 3 review; the mockup's messages are short).</summary>
    public const double UserMaxWidthPercent = 75;

    /// <summary>.composer padding: 0 1.5ch 0 2ch. The prompt sits 2 columns inside the border.</summary>
    public const int ComposerPaddingCols = 2;

    /// <summary>Code block, user message and composer: text one row below the shape's top (the
    /// nearest whole row to pre.code's .75 and the composer's 2.5-row height).</summary>
    public const int ShapePaddingRows = 1;

    /// <summary>Pill caps (one-line user message, APPROVED, tool chip): 2 columns each, the
    /// half-round end and the mockup's ~1.6ch padding. A half circle of the cell height is wider
    /// than one column on every display.</summary>
    public const int PillCapCols = 2;

    /// <summary>.keys margin-top: calc(var(--cell-h) * .6). One blank row above the key bar.</summary>
    public const int KeyBarGapRows = 1;

    public static ForgeTheme Light { get; } = new()
    {
        Surface = Color.Rgb(0xf7, 0xf8, 0xfe),
        SurfaceHeader = Color.Rgb(0xf8, 0xfa, 0xff),
        SurfaceAlt = Color.Rgb(0xf1, 0xf4, 0xfb),
        CardSurface = Color.Rgb(0xff, 0xff, 0xff),
        Text = Color.Rgb(0x10, 0x1d, 0x34),
        TextStrong = Color.Rgb(0x10, 0x1d, 0x34),
        TextMuted = Color.Rgb(0x63, 0x74, 0x8c),
        CardTitle = Color.Rgb(0x5b, 0x6b, 0x83),
        Accent = Color.Rgb(0x0f, 0x6f, 0xeb),
        Prompt = Color.Rgb(0x68, 0x9d, 0xf1),
        Border = Color.Rgb(0xd5, 0xda, 0xe5),
        CardBorder = Color.Rgb(0xe7, 0xec, 0xf4),
        Selection = Color.Rgb(0xdb, 0xe7, 0xfb),
        UserPillFill = Color.Rgb(0xef, 0xf5, 0xfe),
        UserPillText = Color.Rgb(0x10, 0x3f, 0x87),
        Success = Color.Rgb(0x4e, 0x7c, 0x0f),
        SuccessFill = Color.Rgb(0xf2, 0xfb, 0xe6),
        Warning = Color.Rgb(0xb4, 0x53, 0x09),
        Error = Color.Rgb(0xb9, 0x1c, 0x1c),
        CodeBlockFill = Color.Rgb(0xf7, 0xf8, 0xfe),
        CodeBlockBorder = Color.Rgb(0xe7, 0xec, 0xf4),
        CodeBlockText = Color.Rgb(0x10, 0x1d, 0x34),
        InlineCode = Color.Rgb(0x0f, 0x6f, 0xeb),
        Link = Color.Rgb(0x0f, 0x6f, 0xeb),
        ToolFill = Color.Rgb(0xec, 0xef, 0xf6),
        // .card border-radius: 14px; border: 1px; --shadow: 0 1px 2px rgba(16,29,52,.05), 0 6px 20px rgba(16,29,52,.07)
        CardRadius = 14,
        CardHairline = 1,
        CardShadowNear = new(Color.Rgb(0x10, 0x1d, 0x34), Alpha: 0.05, OffsetY: 1, Blur: 2),
        CardShadowFar = new(Color.Rgb(0x10, 0x1d, 0x34), Alpha: 0.07, OffsetY: 6, Blur: 20),
        // pre.code border-radius: 10px; border: 1px. .you .bubble border-radius: 14px.
        CodeBlockRadius = 10,
        CodeBlockHairline = 1,
        UserRadius = 14,
        // .composer border-radius: 14px; border: 1px solid var(--accent);
        // box-shadow: 0 0 0 4px color-mix(in srgb, var(--accent) 14%, transparent) (no drop shadow, Ameer)
        ComposerRadius = 14,
        ComposerHairline = 1,
        ComposerGlow = new(Color.Rgb(0x0f, 0x6f, 0xeb), Alpha: 0.14, Spread: 4),
        // .pill-ok::before width: 6px; height: 6px
        PillDotDiameter = 6,
        ImageIdSlot = 0,
    };

    public static ForgeTheme Dark { get; } = new()
    {
        Surface = Color.Rgb(0x0f, 0x16, 0x22),
        SurfaceHeader = Color.Rgb(0x11, 0x1a, 0x28),
        SurfaceAlt = Color.Rgb(0x14, 0x1d, 0x2b),
        CardSurface = Color.Rgb(0x15, 0x1f, 0x2e),
        Text = Color.Rgb(0xc9, 0xd4, 0xe3),
        TextStrong = Color.Rgb(0xe8, 0xee, 0xf7),
        TextMuted = Color.Rgb(0x6b, 0x7a, 0x91),
        CardTitle = Color.Rgb(0x6b, 0x7a, 0x91),
        Accent = Color.Rgb(0x4f, 0x9b, 0xff),
        Prompt = Color.Rgb(0x24, 0xd5, 0xee),
        Border = Color.Rgb(0x24, 0x30, 0x44),
        CardBorder = Color.Rgb(0x22, 0x30, 0x4a),
        Selection = Color.Rgb(0x16, 0x34, 0x5a),
        UserPillFill = Color.Rgb(0x16, 0x34, 0x5a),
        UserPillText = Color.Rgb(0xe8, 0xee, 0xf7),
        Success = Color.Rgb(0x8c, 0xc1, 0x52),
        SuccessFill = Color.Rgb(0x1d, 0x2a, 0x1a),
        Warning = Color.Rgb(0xf0, 0xb3, 0x5a),
        Error = Color.Rgb(0xf0, 0x71, 0x78),
        CodeBlockFill = Color.Rgb(0x0f, 0x16, 0x22),
        CodeBlockBorder = Color.Rgb(0x2a, 0x38, 0x50),
        CodeBlockText = Color.Rgb(0xe8, 0xee, 0xf7),
        InlineCode = Color.Rgb(0x4f, 0x9b, 0xff),
        Link = Color.Rgb(0x4f, 0x9b, 0xff),
        ToolFill = Color.Rgb(0x1a, 0x23, 0x33),
        // --shadow: 0 1px 2px rgba(0,0,0,.4), 0 8px 24px rgba(0,0,0,.35)
        CardRadius = 14,
        CardHairline = 1,
        CardShadowNear = new(Color.Rgb(0x00, 0x00, 0x00), Alpha: 0.40, OffsetY: 1, Blur: 2),
        CardShadowFar = new(Color.Rgb(0x00, 0x00, 0x00), Alpha: 0.35, OffsetY: 8, Blur: 24),
        CodeBlockRadius = 10,
        CodeBlockHairline = 1,
        UserRadius = 14,
        ComposerRadius = 14,
        ComposerHairline = 1,
        ComposerGlow = new(Color.Rgb(0x4f, 0x9b, 0xff), Alpha: 0.14, Spread: 4),
        PillDotDiameter = 6,
        ImageIdSlot = 1,
    };

    public required Color Surface { get; init; }
    public required Color SurfaceHeader { get; init; }
    public required Color SurfaceAlt { get; init; }
    public required Color CardSurface { get; init; }
    public required Color Text { get; init; }
    public required Color TextStrong { get; init; }
    public required Color TextMuted { get; init; }
    public required Color CardTitle { get; init; }
    public required Color Accent { get; init; }
    public required Color Prompt { get; init; }
    public required Color Border { get; init; }
    public required Color CardBorder { get; init; }
    public required Color Selection { get; init; }
    public required Color UserPillFill { get; init; }
    public required Color UserPillText { get; init; }
    public required Color Success { get; init; }
    public required Color SuccessFill { get; init; }
    public required Color Warning { get; init; }
    public required Color Error { get; init; }

    // Markdown replies (53.7) and code blocks.
    public required Color CodeBlockFill { get; init; }
    public required Color CodeBlockBorder { get; init; }
    public required Color CodeBlockText { get; init; }
    public required Color InlineCode { get; init; }
    public required Color Link { get; init; }

    /// <summary>The tool (hands) line chip's fill (Phase 56 Task 3; no outline).</summary>
    public required Color ToolFill { get; init; }

    // Card edges (Phase 56), drawn as image tiles: mockup px.
    public required double CardRadius { get; init; }
    public required double CardHairline { get; init; }
    public required CardShadow CardShadowNear { get; init; }
    public required CardShadow CardShadowFar { get; init; }

    // Other shapes (Phase 56 Task 3), drawn as image tiles: mockup px.
    public required double CodeBlockRadius { get; init; }
    public required double CodeBlockHairline { get; init; }
    public required double UserRadius { get; init; }
    public required double ComposerRadius { get; init; }
    public required double ComposerHairline { get; init; }
    public required RingGlow ComposerGlow { get; init; }

    /// <summary>The APPROVED pill's dot, drawn in its left cap.</summary>
    public required double PillDotDiameter { get; init; }

    /// <summary>The theme's part of the image ids (0 or 1, one per theme), so two themes never
    /// share an image id (TileSet.ImageIds).</summary>
    public required int ImageIdSlot { get; init; }
}

/// <summary>One CSS box-shadow layer under a card: an opaque colour at <paramref name="Alpha"/>,
/// moved down by <paramref name="OffsetY"/> and blurred by <paramref name="Blur"/> (the CSS blur
/// radius), both in mockup px. The mockup's card shadows have no x offset.</summary>
internal readonly record struct CardShadow(Color Color, double Alpha, double OffsetY, double Blur);

/// <summary>A hard focus glow around a shape's border (CSS box-shadow 0 0 0 spread): the colour at
/// <paramref name="Alpha"/>, <paramref name="Spread"/> mockup px wide, no blur.</summary>
internal readonly record struct RingGlow(Color Color, double Alpha, double Spread);
