using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Styling;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (53.5): the one dark theme as a named token map, taken from the accepted mockup
// (docs/design/forge_tui_first_slice_mockup.html). Every colour on screen comes from these tokens;
// the styles below and ChatScreen reference tokens only, never a literal colour.
internal static class ForgeTheme
{
    public static readonly Color Bg = Color.Rgb(0x0f, 0x16, 0x22);
    public static readonly Color Text = Color.Rgb(0xc9, 0xd4, 0xe3);
    public static readonly Color Muted = Color.Rgb(0x6b, 0x7a, 0x91);
    public static readonly Color Accent = Color.Rgb(0x4f, 0x9b, 0xff);
    public static readonly Color Success = Color.Rgb(0x8c, 0xc1, 0x52);
    public static readonly Color Warning = Color.Rgb(0xf0, 0xb3, 0x5a);
    public static readonly Color Bright = Color.Rgb(0xe8, 0xee, 0xf7);
    public static readonly Color Border = Color.Rgb(0x24, 0x30, 0x44);
    public static readonly Color Bar = Color.Rgb(0x1a, 0x22, 0x30);
    public static readonly Color Selection = Color.Rgb(0x16, 0x34, 0x5a);
    public static readonly Color CardBorder = Color.Rgb(0x2a, 0x38, 0x50);
    public static readonly Color Prompt = Color.Rgb(0x24, 0xd5, 0xee);

    /// <summary>The XenoAtom theme for the whole screen, so built-in control parts (editor fill,
    /// scroll bar, focus) also resolve to the tokens.</summary>
    public static Theme Screen { get; } = new()
    {
        Foreground = Text,
        Background = Bg,
        Surface = Bg,
        SurfaceAlt = Bar,
        PopupSurface = Bar,
        InputFill = Bg,
        InputFillFocused = Bg,
        ControlFill = Bar,
        ControlFillHover = Bar,
        ControlFillPressed = Selection,
        Border = Border,
        FocusBorder = Border,
        Accent = Accent,
        Primary = Accent,
        Selection = Selection,
        Success = Success,
        Warning = Warning,
        Error = Warning,
        Muted = Muted,
        Disabled = Muted,
    };

    public static TextBlockStyle Foreground(Color color) => TextBlockStyle.Default with { Foreground = color };

    public static Style PillText { get; } = Style.None.WithForeground(Bright).WithBackground(Selection);

    public static Style CardText { get; } = Style.None.WithForeground(Bright);

    public static Style NoticeText { get; } = Style.None.WithForeground(Muted) | TextStyle.Italic;

    public static TextBlockStyle Progress { get; } = TextBlockStyle.Default with { Foreground = Muted, TextStyle = TextStyle.Italic };

    public static TextBlockStyle KeyBar { get; } = TextBlockStyle.Default with { Foreground = Muted, Background = Bar, FillBackground = true };

    public static Style CardFrame { get; } = Style.None.WithForeground(CardBorder);

    public static RuleStyle Divider { get; } = RuleStyle.Default with { LineStyle = Style.None.WithForeground(Border) };

    public static PromptEditorStyle Composer { get; } = PromptEditorStyle.Default with
    {
        PromptForeground = Prompt,
        PlaceholderForeground = Muted,
        Background = Bg,
        PromptSidebarBackground = Bg,
        Selection = Selection,
        ShowPromptSeparator = false,
    };

    public static ScrollViewerStyle Scroll { get; } = ScrollViewerStyle.Default with
    {
        TrackStyle = Style.None.WithForeground(Bg).WithBackground(Bg),
        ThumbStyle = Style.None.WithForeground(Border).WithBackground(Bg),
    };
}
