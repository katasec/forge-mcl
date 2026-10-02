using ForgeMission.Cli.Tui.Graphics;
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

    /// <summary>The APPROVED pill: Success text on SuccessFill between caps with the status dot.</summary>
    public Pill Approved { get; } = new(
        TextBlockStyle.Default with { Foreground = theme.Success, Background = theme.SuccessFill },
        Style.None.WithBackground(theme.SuccessFill));

    public CapShape ApprovedCaps { get; } = new(theme.SurfaceHeader, theme.SuccessFill, theme.Success, theme.PillDotDiameter);

    public RuleStyle Divider { get; } = RuleStyle.Default with { LineStyle = Style.None.WithForeground(theme.Border) };

    // ── Transcript ──────────────────────────────────────────────────────────────────────────

    /// <summary>A user message's text on its fill: a capped pill on one line, a ring once it wraps.</summary>
    public Style UserText { get; } = Style.None.WithForeground(theme.UserPillText).WithBackground(theme.UserPillFill);

    public Style UserFill { get; } = Style.None.WithBackground(theme.UserPillFill);

    public CapShape UserCaps { get; } = new(theme.Surface, theme.UserPillFill, null, 0);

    /// <summary>A multi-line user message: radius 14, fill only (no border, no shadow).</summary>
    public RingShape UserShape { get; } = new(theme.Surface, theme.UserPillFill, theme.UserPillFill, theme.UserRadius, 0,
        [], null, ForgeTheme.UserPaddingCols, ForgeTheme.ShapePaddingRows, 0);

    /// <summary>A tool (hands) line: a one-row chip, muted text on ToolFill, no outline.</summary>
    public Pill Tool { get; } = new(
        TextBlockStyle.Default with { Foreground = theme.TextMuted, Background = theme.ToolFill },
        Style.None.WithBackground(theme.ToolFill));

    public CapShape ToolCaps { get; } = new(theme.Surface, theme.ToolFill, null, 0);

    /// <summary>The widest a user message may be, as a share of the transcript.</summary>
    public double UserMaxWidthPercent => ForgeTheme.UserMaxWidthPercent;

    public TextBlockStyle YouLabel { get; } = Foreground(theme.TextMuted);

    /// <summary>The card's text cells (and the ring's plain cells): the card surface.</summary>
    public Style CardFill { get; } = Style.None.WithBackground(theme.CardSurface);

    /// <summary>What the card edge tiles are drawn from (Phase 56). The card's downward shadow puts
    /// its bottom border high in its tile, so it gets one more plain row below its content than
    /// the ring gives: the mockup's card padding is equal top and bottom.</summary>
    public RingShape CardShape { get; } = new(theme.Surface, theme.CardSurface, theme.CardBorder,
        theme.CardRadius, theme.CardHairline, [theme.CardShadowNear, theme.CardShadowFar], null,
        ForgeTheme.CardPaddingCols, ForgeTheme.CardPaddingRows, ForgeTheme.CardPaddingRows);

    /// <summary>The theme's part of the image ids (TileSet.ImageIds).</summary>
    public int ImageIdSlot { get; } = theme.ImageIdSlot;

    /// <summary>The transcript gutter in columns; a card's border lands in this column.</summary>
    public int TranscriptGutterCols => ForgeTheme.TranscriptGutterCols;

    /// <summary>Blank rows above each card.</summary>
    public int CardGapRows => ForgeTheme.CardGapRows;

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

    /// <summary>Fenced and indented code in replies: the code on its fill, no language label.</summary>
    public CodeBlockStyle CodeBlock { get; } = new(
        Style.None.WithBackground(theme.CodeBlockFill),
        Style.None.WithForeground(theme.CodeBlockText).WithBackground(theme.CodeBlockFill));

    /// <summary>A code block's ring: radius 10, hairline CodeBlockBorder, no shadow, on the card.</summary>
    public RingShape CodeBlockShape { get; } = new(theme.CardSurface, theme.CodeBlockFill, theme.CodeBlockBorder,
        theme.CodeBlockRadius, theme.CodeBlockHairline, [], null, ForgeTheme.CodeBlockPaddingCols, ForgeTheme.ShapePaddingRows, 0);

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
        Background = theme.CardSurface,
        PromptSidebarBackground = theme.CardSurface,
        Selection = theme.Selection,
        ShowPromptSeparator = false,
    };

    /// <summary>The composer's text cells (and its ring's plain cells): the card surface.</summary>
    public Style ComposerFill { get; } = Style.None.WithBackground(theme.CardSurface);

    /// <summary>The composer's ring: radius 14, hairline Accent, a 4 px Accent glow, no shadow.</summary>
    public RingShape ComposerShape { get; } = new(theme.Surface, theme.CardSurface, theme.Accent,
        theme.ComposerRadius, theme.ComposerHairline, [], theme.ComposerGlow, ForgeTheme.ComposerPaddingCols,
        ForgeTheme.ShapePaddingRows, 0);

    /// <summary>The key bar: muted text on the screen surface, no fill of its own.</summary>
    public TextBlockStyle KeyBar { get; } = Foreground(theme.TextMuted);

    /// <summary>Blank rows above the key bar.</summary>
    public int KeyBarGapRows => ForgeTheme.KeyBarGapRows;

    private static TextBlockStyle Foreground(Color color) => TextBlockStyle.Default with { Foreground = color };

    private static MarkdownAlertStyle Alert(Color token, Color surface) => MarkdownAlertStyle.Default with
    {
        BorderStyle = Style.None.WithForeground(token),
        TitleStyle = Style.None.WithForeground(token) | TextStyle.Bold,
        BackgroundStyle = Style.None.WithBackground(surface),
    };
}

/// <summary>A reply code block: fill and text (its ring is <see cref="ForgeStyles.CodeBlockShape"/>).</summary>
internal sealed record CodeBlockStyle(Style Fill, Style Text);

/// <summary>A one-row pill: the text on its fill, between cap tiles drawn in the fill colour.</summary>
internal sealed record Pill(TextBlockStyle Text, Style Fill);
