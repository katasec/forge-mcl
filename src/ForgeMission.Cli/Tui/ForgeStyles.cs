using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Extensions.Markdown.Styling;
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

    /// <summary>Participant replies (53.7). Every slot carries an explicit token: a slot left at
    /// the package default would fall back to the package's theme-derived colours.</summary>
    public MarkdownStyle Markdown { get; } = MarkdownStyle.Default with
    {
        ParagraphStyle = Style.None.WithForeground(theme.TextStrong),
        Heading1Style = Style.None.WithForeground(theme.TextStrong) | TextStyle.Bold,
        Heading2Style = Style.None.WithForeground(theme.TextStrong) | TextStyle.Bold,
        Heading3Style = Style.None.WithForeground(theme.TextStrong) | TextStyle.Bold,
        Heading4Style = Style.None.WithForeground(theme.TextStrong) | TextStyle.Bold,
        Heading5Style = Style.None.WithForeground(theme.TextStrong) | TextStyle.Bold,
        Heading6Style = Style.None.WithForeground(theme.TextStrong) | TextStyle.Bold,
        StrongStyle = Style.None.WithForeground(theme.TextStrong) | TextStyle.Bold,
        EmphasisStyle = Style.None | TextStyle.Italic,
        InlineCodeStyle = Style.None.WithForeground(theme.InlineCode),
        LinkStyle = Style.None.WithForeground(theme.Link) | TextStyle.Underline,
        QuotePrefixStyle = Style.None.WithForeground(theme.TextMuted),
        HtmlStyle = Style.None.WithForeground(theme.TextMuted),
        NoteAlert = Alert(theme.Accent, theme.CardSurface),
        TipAlert = Alert(theme.Success, theme.CardSurface),
        ImportantAlert = Alert(theme.Accent, theme.CardSurface),
        WarningAlert = Alert(theme.Warning, theme.CardSurface),
        CautionAlert = Alert(theme.Error, theme.CardSurface),
    };

    /// <summary>Fenced and indented code in replies: a rounded box, no language label. The
    /// border cells sit on the card surface so the fill stays inside the line.</summary>
    public CodeBlockStyle CodeBlock { get; } = new(
        Style.None.WithForeground(theme.CodeBlockBorder).WithBackground(theme.CardSurface),
        Style.None.WithBackground(theme.CodeBlockFill),
        Style.None.WithForeground(theme.CodeBlockText).WithBackground(theme.CodeBlockFill));

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

    private static MarkdownAlertStyle Alert(Color token, Color surface) => MarkdownAlertStyle.Default with
    {
        BorderStyle = Style.None.WithForeground(token),
        TitleStyle = Style.None.WithForeground(token) | TextStyle.Bold,
        BackgroundStyle = Style.None.WithBackground(surface),
    };
}

/// <summary>A reply code block: border, fill, and text.</summary>
internal sealed record CodeBlockStyle(Style Border, Style Fill, Style Text);

/// <summary>A one-line pill: the text on its fill, between rounded caps drawn in the fill colour.</summary>
internal sealed record Pill(TextBlockStyle Text, TextBlockStyle Cap, string CapLeft, string CapRight);
