using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Styling;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (53.6): every component style, built from one ForgeTheme in one place. ChatScreen
// uses only these; it never names a colour or a theme.
internal sealed class ForgeStyles(ForgeTheme theme)
{
    /// <summary>The XenoAtom theme for the whole screen, so built-in control parts (editor fill,
    /// scroll bar, focus, flow borders) resolve to the tokens too. Cards get rounded corners.</summary>
    public Theme Screen { get; } = new()
    {
        Foreground = theme.Text,
        Background = theme.Surface,
        Surface = theme.Surface,
        SurfaceAlt = theme.SurfaceAlt,
        PopupSurface = theme.SurfaceAlt,
        InputFill = theme.Surface,
        InputFillFocused = theme.Surface,
        ControlFill = theme.SurfaceAlt,
        ControlFillHover = theme.SurfaceAlt,
        ControlFillPressed = theme.Selection,
        Border = theme.Border,
        FocusBorder = theme.Border,
        Accent = theme.Accent,
        Primary = theme.Accent,
        Selection = theme.Selection,
        Success = theme.Success,
        Warning = theme.Warning,
        Error = theme.Error,
        Muted = theme.TextMuted,
        Disabled = theme.TextMuted,
        Lines = LineGlyphs.Rounded,
    };

    // ── Header ──────────────────────────────────────────────────────────────────────────────

    public HeaderStyle Header { get; } = HeaderStyle.Default with { Background = theme.SurfaceHeader, Foreground = theme.Text };

    public TextBlockStyle Brand { get; } = Foreground(theme.Accent);

    public TextBlockStyle Label { get; } = Foreground(theme.TextMuted);

    public TextBlockStyle Project { get; } = Foreground(theme.TextStrong);

    public Pill Approved { get; } = new(
        TextBlockStyle.Default with { Foreground = theme.Success, Background = theme.SuccessFill },
        Foreground(theme.SuccessFill), theme.PillCapLeft, theme.PillCapRight);

    public RuleStyle Divider { get; } = RuleStyle.Default with { LineStyle = Style.None.WithForeground(theme.Border) };

    // ── Transcript ──────────────────────────────────────────────────────────────────────────

    public Pill User { get; } = new(
        TextBlockStyle.Default with { Foreground = theme.UserPillText, Background = theme.UserPillFill },
        Foreground(theme.UserPillFill), theme.PillCapLeft, theme.PillCapRight);

    /// <summary>A multi-line user message: the filled block without caps.</summary>
    public Style UserBlock { get; } = Style.None.WithForeground(theme.UserPillText).WithBackground(theme.UserPillFill);

    public TextBlockStyle YouLabel { get; } = Foreground(theme.TextMuted);

    public Style CardFrame { get; } = Style.None.WithForeground(theme.CardBorder);

    public Style CardFill { get; } = Style.None.WithBackground(theme.CardSurface);

    public TextBlockStyle CardTitle { get; } = Foreground(theme.CardTitle);

    public Style CardText { get; } = Style.None.WithForeground(theme.TextStrong);

    public Style Notice { get; } = Style.None.WithForeground(theme.TextMuted) | TextStyle.Italic;

    public Style ErrorNotice { get; } = Style.None.WithForeground(theme.Error);

    public ScrollViewerStyle Scroll { get; } = ScrollViewerStyle.Default with
    {
        TrackStyle = Style.None.WithForeground(theme.Surface).WithBackground(theme.Surface),
        ThumbStyle = Style.None.WithForeground(theme.Border).WithBackground(theme.Surface),
    };

    // ── Composer and key bar ────────────────────────────────────────────────────────────────

    public TextBlockStyle Progress { get; } = TextBlockStyle.Default with { Foreground = theme.TextMuted, TextStyle = TextStyle.Italic };

    public PromptEditorStyle Composer { get; } = PromptEditorStyle.Default with
    {
        PromptForeground = theme.Prompt,
        PlaceholderForeground = theme.TextMuted,
        Background = theme.Surface,
        PromptSidebarBackground = theme.Surface,
        Selection = theme.Selection,
        ShowPromptSeparator = false,
    };

    public TextBlockStyle KeyBar { get; } = TextBlockStyle.Default with
    {
        Foreground = theme.TextMuted,
        Background = theme.SurfaceAlt,
        FillBackground = true,
    };

    private static TextBlockStyle Foreground(Color color) => TextBlockStyle.Default with { Foreground = color };
}

/// <summary>A one-line pill: the text on its fill, between rounded caps drawn in the fill colour.</summary>
internal sealed record Pill(TextBlockStyle Text, TextBlockStyle Cap, string CapLeft, string CapRight);
