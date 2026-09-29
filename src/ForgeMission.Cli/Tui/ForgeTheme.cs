using XenoAtom.Terminal.UI;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (53.6): a theme is data — purpose-named colour tokens plus the pill cap glyphs.
// This file is the only place a colour literal appears; ForgeStyles builds every component style
// from a ForgeTheme. A new theme is one more instance here plus its name in ForgeConfig.
// Light is sampled from docs/design/forge_tui_light_mockup.png; Dark is the 53.5 mockup's palette.
internal sealed record ForgeTheme
{
    public static ForgeTheme Light { get; } = new()
    {
        Surface = Color.Rgb(0xf7, 0xf8, 0xfe),
        SurfaceHeader = Color.Rgb(0xf8, 0xfa, 0xff),
        SurfaceAlt = Color.Rgb(0xf8, 0xfa, 0xff),
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
        PillCapLeft = "",
        PillCapRight = "",
    };

    public static ForgeTheme Dark { get; } = new()
    {
        Surface = Color.Rgb(0x0f, 0x16, 0x22),
        SurfaceHeader = Color.Rgb(0x0f, 0x16, 0x22),
        SurfaceAlt = Color.Rgb(0x1a, 0x22, 0x30),
        CardSurface = Color.Rgb(0x0f, 0x16, 0x22),
        Text = Color.Rgb(0xc9, 0xd4, 0xe3),
        TextStrong = Color.Rgb(0xe8, 0xee, 0xf7),
        TextMuted = Color.Rgb(0x6b, 0x7a, 0x91),
        CardTitle = Color.Rgb(0x6b, 0x7a, 0x91),
        Accent = Color.Rgb(0x4f, 0x9b, 0xff),
        Prompt = Color.Rgb(0x24, 0xd5, 0xee),
        Border = Color.Rgb(0x24, 0x30, 0x44),
        CardBorder = Color.Rgb(0x2a, 0x38, 0x50),
        Selection = Color.Rgb(0x16, 0x34, 0x5a),
        UserPillFill = Color.Rgb(0x16, 0x34, 0x5a),
        UserPillText = Color.Rgb(0xe8, 0xee, 0xf7),
        Success = Color.Rgb(0x8c, 0xc1, 0x52),
        SuccessFill = Color.Rgb(0x1d, 0x2a, 0x1a),
        Warning = Color.Rgb(0xf0, 0xb3, 0x5a),
        Error = Color.Rgb(0xf0, 0x71, 0x78),
        CodeBlockFill = Color.Rgb(0x13, 0x1c, 0x2b),
        CodeBlockBorder = Color.Rgb(0x2a, 0x38, 0x50),
        CodeBlockText = Color.Rgb(0xc9, 0xd4, 0xe3),
        InlineCode = Color.Rgb(0x4f, 0x9b, 0xff),
        Link = Color.Rgb(0x4f, 0x9b, 0xff),
        PillCapLeft = "",
        PillCapRight = "",
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

    // Markdown (the next step): defined so both themes are complete, not used yet.
    public required Color CodeBlockFill { get; init; }
    public required Color CodeBlockBorder { get; init; }
    public required Color CodeBlockText { get; init; }
    public required Color InlineCode { get; init; }
    public required Color Link { get; init; }

    /// <summary>Rounded pill ends (Nerd Font U+E0B6 / U+E0B4, one cell each). Falling back to
    /// half blocks is a change here only.</summary>
    public required string PillCapLeft { get; init; }
    public required string PillCapRight { get; init; }
}
