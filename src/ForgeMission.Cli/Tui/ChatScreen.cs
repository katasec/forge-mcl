using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Text;

namespace ForgeMission.Cli.Tui;

/// <summary>What the header shows: the Project, the mission and its version, and the provider
/// profile the mission's definition pins.</summary>
internal sealed record ChatHeader(string Project, string Mission, int Version, string Profile);

// forge chat TUI (53.5): every visual on the screen — header, transcript, progress line, composer,
// key bar — laid out as the accepted mockup. It renders transcript blocks; it decides nothing
// about events (Transcript) or turns (ChatTui).
internal sealed class ChatScreen
{
    private const string PendingBody = "▌";
    private const string Keys = "enter send · shift+enter newline · pgup/pgdn scroll · ctrl-c stop · ctrl-d quit";

    private readonly DocumentFlow _flow = new DocumentFlow().ItemSpacing(0);
    private readonly State<string> _progress = new("");
    // One entry per rendered block: the body of a card, null for other blocks.
    private readonly List<Paragraph?> _cardBodies = [];

    public ChatScreen(ChatHeader header)
    {
        Composer = BuildComposer(header);
        Root = new DockLayout()
            .Top(new VStack(BuildHeader(header), Divider()))
            .Content(_flow.Style(ForgeTheme.Scroll))
            .Bottom(new VStack(
                new TextBlock(() => _progress.Value).Style(ForgeTheme.Progress).Margin(new Thickness(1, 0, 1, 0)),
                Divider(),
                Composer,
                new TextBlock($" {Keys}").Style(ForgeTheme.KeyBar)));
        Root.Style(ForgeTheme.Screen);
    }

    public Visual Root { get; }

    public PromptEditor Composer { get; }

    /// <summary>Brings the screen in line with <paramref name="blocks"/>. Blocks only append, and
    /// only a card's text changes in place, so rendered items are kept and updated.</summary>
    public void Show(IReadOnlyList<TranscriptBlock> blocks)
    {
        for (var i = _cardBodies.Count; i < blocks.Count; i++)
            _cardBodies.Add(AppendBlock(blocks[i]));

        for (var i = 0; i < blocks.Count; i++)
        {
            if (_cardBodies[i] is { } body && blocks[i] is ParticipantCard card)
                SetText(body, card.Text ?? (i == blocks.Count - 1 ? PendingBody : ""), ForgeTheme.CardText);
        }

        _progress.Value = Transcript.Replying(blocks) is { } expert ? $"{expert} is replying …" : "";
    }

    public void PageUp()
    {
        _flow.FollowTail = false;
        _flow.Scroll.ScrollBy(0, -Page);
    }

    public void PageDown()
    {
        _flow.Scroll.ScrollBy(0, Page);
        if (_flow.Scroll.OffsetY >= _flow.Scroll.ExtentHeight - _flow.Scroll.ViewportHeight)
            _flow.ScrollToTail();
    }

    private int Page => Math.Max(1, _flow.Scroll.ViewportHeight - 1);

    // ── Blocks ──────────────────────────────────────────────────────────────────────────────

    private Paragraph? AppendBlock(TranscriptBlock block)
    {
        switch (block)
        {
            case YouBlock you:
                _flow.Items.Add(YouItem(you.Text));
                return null;
            case ParticipantCard card:
                var body = Text("", ForgeTheme.CardText);
                _flow.Items.Add(CardItem(card.Title, body));
                return body;
            case NoticeLine notice:
                _flow.Items.Add(NoticeItem(notice.Text));
                return null;
            default:
                throw new InvalidOperationException($"No view for {block.GetType().Name}.");
        }
    }

    private static DocumentFlowItem YouItem(string text) => new()
    {
        Content = new FlowDocument().Add(new HStack(
                Text($" {text} ", ForgeTheme.PillText),
                new TextBlock("You").Style(ForgeTheme.Foreground(ForgeTheme.Muted)))
            .Spacing(1)
            .HorizontalAlignment(Align.End)),
        Alignment = DocumentFlowAlignment.Right,
        Padding = new Thickness(1, 0, 1, 0),
    };

    private static DocumentFlowItem CardItem(string title, Paragraph body) => new()
    {
        Content = new FlowDocument()
            .Add(new TextBlock(title).Style(ForgeTheme.Foreground(ForgeTheme.Muted)))
            .Add(body),
        Alignment = DocumentFlowAlignment.Left,
        MaxWidthPercent = 100,
        // The border is drawn in the padding's outer cells; the inner column keeps text off it.
        Padding = new Thickness(2, 1, 2, 1),
        BorderStyle = ForgeTheme.CardFrame,
    };

    private static DocumentFlowItem NoticeItem(string text) => new()
    {
        Content = new FlowDocument().Add(Text(text, ForgeTheme.NoticeText)),
        Alignment = DocumentFlowAlignment.Left,
        Padding = new Thickness(1, 0, 1, 0),
    };

    /// <summary>Wrapped text that keeps its line breaks (TextBlock folds them into spaces), in one
    /// token style.</summary>
    private static Paragraph Text(string text, Style style)
    {
        var paragraph = new Paragraph { Wrap = true };
        SetText(paragraph, text, style);
        return paragraph;
    }

    private static void SetText(Paragraph paragraph, string text, Style style)
    {
        if (paragraph.Text == text) return;
        paragraph.Text = text;
        paragraph.Runs = [new StyledRun(0, text.Length, style)];
    }

    // ── Chrome ──────────────────────────────────────────────────────────────────────────────

    private static Visual BuildHeader(ChatHeader header) => new Grid()
        .Columns(new ColumnDefinition { Width = GridLength.Star() }, new ColumnDefinition { Width = GridLength.Auto })
        .Rows(new RowDefinition { Height = GridLength.Auto })
        .Cell(new HStack(
                new TextBlock("forge").Style(ForgeTheme.Foreground(ForgeTheme.Accent)),
                new TextBlock("│ PROJECT").Style(ForgeTheme.Foreground(ForgeTheme.Muted)),
                new TextBlock(header.Project).Style(ForgeTheme.Foreground(ForgeTheme.Bright)))
            .Spacing(1), row: 0, column: 0)
        .Cell(new HStack(
                new TextBlock($"{header.Mission.ToUpperInvariant()} · V{header.Version} · ").Style(ForgeTheme.Foreground(ForgeTheme.Muted)),
                new TextBlock("APPROVED").Style(ForgeTheme.Foreground(ForgeTheme.Success)),
                new TextBlock($" · {header.Profile}").Style(ForgeTheme.Foreground(ForgeTheme.Muted))), row: 0, column: 1)
        .Margin(new Thickness(1, 0, 1, 0));

    private static PromptEditor BuildComposer(ChatHeader header)
    {
        // Shift+Enter is the only newline gesture: the editor attaches before the kitty keyboard
        // probe completes, and the default fallback would otherwise rebind newline to Ctrl+N.
        var composer = new PromptEditor(PromptEditorConfig.Default with { InsertNewLineFallbackGesture = null })
            .PromptMarkup("›")
            .Placeholder($"Message {header.Mission} v{header.Version}…")
            .AutoSizeMode(TextEditorAutoSizeMode.Height)
            .MinHeight(1)
            .MaxHeight(6)
            .Style(ForgeTheme.Composer);
        // Ctrl-C belongs to the screen (stop the run); a mouse selection is still copied by the app.
        composer.RemoveCommand("TextEditor.Copy");
        return composer;
    }

    private static Rule Divider() => new Rule().Style(ForgeTheme.Divider);
}
