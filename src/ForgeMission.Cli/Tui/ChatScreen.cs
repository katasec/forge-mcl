using ForgeMission.Cli.Tui.Graphics;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Text;

namespace ForgeMission.Cli.Tui;

/// <summary>What the header shows: the Project, the mission and its version, and the provider
/// profile the mission's definition pins.</summary>
internal sealed record ChatHeader(string Project, string Mission, int Version, string Profile);

// forge chat TUI (53.5, 53.6): every visual on the screen — header, transcript, progress line,
// composer, key bar — laid out as the accepted mockups. It renders transcript blocks; it decides
// nothing about events (Transcript) or turns (ChatTui), and takes every style from ForgeStyles.
// Shapes (Phase 56) — cards, code blocks, user messages, the APPROVED pill, tool chips, the
// composer — are framed by image tiles (TileFrame); this file only places them, with the mockup's
// gutter and gap, and never builds pixels. It holds the tile-set registry (ScreenTiles), which
// arrives on the TUI's first tick (UseTiles), before any block is shown: ChatTui opens the
// conversation only after it, and Enter does nothing until HasTiles. The header pill and the
// composer frame are placed into their slots then, and the code-block renderer is created then.
internal sealed class ChatScreen
{
    private const string PendingBody = "▌";
    private const string YouLabel = "You";
    private const string Keys = "enter send · shift+enter newline · pgup/pgdn scroll · ctrl-c stop · ctrl-d quit";

    private readonly DocumentFlow _flow = new DocumentFlow().ItemSpacing(0);
    private readonly State<string> _progress = new("");
    // The blocks on screen, and per block the body of a card (null for other blocks).
    private readonly List<TranscriptBlock> _shown = [];
    private readonly List<MarkdownControl?> _cardBodies = [];
    private readonly ForgeStyles _styles;
    // Filled by UseTiles: the APPROVED pill in the header, the composer's frame.
    private readonly Padder _approvedSlot = new();
    private readonly Padder _composerSlot = new() { HorizontalAlignment = Align.Stretch };
    private ForgeCodeBlockRenderer? _codeBlocks;
    private ScreenTiles? _tiles;

    public ChatScreen(ChatHeader header, ForgeStyles styles)
    {
        _styles = styles;
        Composer = BuildComposer(header);
        // Progress row, composer ring, one blank row, key bar; all on the transcript's gutter.
        var gutter = styles.TranscriptGutterCols;
        Root = new DockLayout()
            .Top(new VStack(BuildHeader(header), Divider()))
            .Content(_flow.Style(styles.Scroll))
            .Bottom(new VStack(
                new TextBlock(() => _progress.Value).Style(styles.Progress).Margin(new Thickness(gutter, 0, 1, 0)),
                _composerSlot,
                new TextBlock(Keys).Style(styles.KeyBar).Margin(new Thickness(gutter, styles.KeyBarGapRows, 1, 0))));
        Root.Style(styles.Screen);
        Root.Style(styles.Markdown);
    }

    public Visual Root { get; }

    public PromptEditor Composer { get; }

    /// <summary>The tile sets every shape is framed with, sent to the terminal before the first
    /// block. Places the header pill and the composer frame, and creates the code-block renderer.</summary>
    public void UseTiles(ScreenTiles tiles)
    {
        _tiles = tiles;
        _codeBlocks = new ForgeCodeBlockRenderer(_styles.CodeBlock, tiles.CodeBlock);
        _approvedSlot.Content = PillOf("APPROVED", _styles.Approved, tiles.Approved);
        _composerSlot.Padding = new Thickness(Gutter(tiles.Composer), 0, Gutter(tiles.Composer), 0);
        _composerSlot.Content = new TileFrame(Composer, tiles.Composer, _styles.ComposerFill, Align.Stretch);
    }

    /// <summary>Whether the tile sets have arrived; no block is shown before them (ChatTui).</summary>
    public bool HasTiles => _tiles is not null;

    /// <summary>Brings the screen in line with <paramref name="blocks"/>. Blocks mostly append and
    /// a card's text changes in place; when a turn ends a pending card can drop out, so items are
    /// kept up to the first block that differs and rebuilt from there.</summary>
    public void Show(IReadOnlyList<TranscriptBlock> blocks)
    {
        var kept = KeptCount(blocks);
        for (var i = _shown.Count - 1; i >= kept; i--)
        {
            _flow.Items.RemoveAt(i);
            _cardBodies.RemoveAt(i);
            _shown.RemoveAt(i);
        }

        for (var i = kept; i < blocks.Count; i++)
        {
            _cardBodies.Add(AppendBlock(blocks[i]));
            _shown.Add(blocks[i]);
        }

        // Live reply deltas (53.8) call this several times a second: SetMarkdown assigns only a card
        // whose text changed, and an unchanged card carries the same string instance, so every other
        // card costs one reference comparison and is never re-parsed.
        for (var i = 0; i < blocks.Count; i++)
        {
            if (_cardBodies[i] is { } body && blocks[i] is ParticipantCard card)
                SetMarkdown(body, card.Text ?? (i == blocks.Count - 1 ? PendingBody : ""));
        }

        _progress.Value = Transcript.Replying(blocks) switch
        {
            null => "",
            "" => "replying …",
            var expert => $"{expert} is replying …",
        };
    }

    /// <summary>How many shown blocks still hold the same place: a card keeps its place while
    /// its title does (its text is updated in place); any other block while it is equal.</summary>
    private int KeptCount(IReadOnlyList<TranscriptBlock> blocks)
    {
        var count = 0;
        while (count < _shown.Count && count < blocks.Count && SamePlace(_shown[count], blocks[count]))
            count++;
        return count;
    }

    private static bool SamePlace(TranscriptBlock shown, TranscriptBlock next) => (shown, next) switch
    {
        (ParticipantCard a, ParticipantCard b) => a.Title == b.Title && a.Mission == b.Mission,
        // Forge's echo of a sent message draws the same pill; keep it rather than redraw.
        (PendingYouBlock a, YouBlock b) => a.Text == b.Text,
        _ => shown == next,
    };

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

    private MarkdownControl? AppendBlock(TranscriptBlock block)
    {
        switch (block)
        {
            case YouBlock you:
                _flow.Items.Add(YouItem(you.Text));
                return null;
            case PendingYouBlock pending:
                _flow.Items.Add(YouItem(pending.Text));
                return null;
            case PendingReplyBlock:
                var pendingBody = CardBody();
                _flow.Items.Add(CardItem(null, pendingBody));
                SetMarkdown(pendingBody, PendingBody);
                return null;
            case ParticipantCard card:
                var body = CardBody();
                _flow.Items.Add(CardItem(card.Title, body));
                return body;
            case NoticeLine notice:
                _flow.Items.Add(LineItem(notice.Text, _styles.Notice));
                return null;
            case ErrorLine error:
                _flow.Items.Add(LineItem(error.Text, _styles.ErrorNotice));
                return null;
            case HandsLine hands:
                _flow.Items.Add(ToolItem(Transcript.HandsText(hands)));
                return null;
            default:
                throw new InvalidOperationException($"No view for {block.GetType().Name}.");
        }
    }

    /// <summary>A message that fits one row is a capped pill; one that wraps or has line breaks is
    /// the user ring. The frame chooses at layout, so a window resize can switch it. The label sits
    /// in a column kept free beside the frame, centred on it, so a wrapping message never covers it.</summary>
    private DocumentFlowItem YouItem(string text) => new()
    {
        Content = new FlowDocument().Add(new ZStack(
                new Padder(TileFrame.OneLineOr(Text(text, _styles.UserText), _tiles!.UserCaps, _tiles.UserRing, _styles.UserFill, Align.End))
                {
                    Padding = new Thickness(0, 0, YouLabel.Length + 1, 0),
                    HorizontalAlignment = Align.End,
                },
                new TextBlock(YouLabel).Style(_styles.YouLabel).HorizontalAlignment(Align.End).VerticalAlignment(Align.Center))
            .HorizontalAlignment(Align.End)),
        Alignment = DocumentFlowAlignment.Right,
        Padding = new Thickness(1, 0, 1, 0),
    };

    /// <summary>A reply body: Markdown in the theme's styles (set on the root), code blocks
    /// through the forge renderer, no scrolling of its own (the transcript scrolls).</summary>
    private MarkdownControl CardBody() => new("")
    {
        HorizontalAlignment = Align.Stretch,
        VerticalAlignment = Align.Start,
        HorizontalScrollEnabled = false,
        VerticalScrollEnabled = false,
        Options = MarkdownRenderOptions.Default with { WrapCodeBlocks = true, CodeBlockRenderer = _codeBlocks! },
    };

    private static void SetMarkdown(MarkdownControl body, string markdown)
    {
        if (body.Markdown != markdown) body.Markdown = markdown;
    }

    /// <summary>A reply card; <paramref name="title"/> is null while no participant has started.
    /// The whole card is one block (its frame), after a blank gap row, inset by the gutter so the
    /// frame's border falls in the transcript's gutter column.</summary>
    private DocumentFlowItem CardItem(string? title, MarkdownControl body) => new()
    {
        Content = new FlowDocument().Add(new TileFrame(CardContent(title, body), _tiles!.Card, _styles.CardFill, Align.Stretch)),
        Alignment = DocumentFlowAlignment.Left,
        MaxWidthPercent = 100,
        Padding = new Thickness(Gutter(_tiles.Card), _styles.CardGapRows, Gutter(_tiles.Card), 0),
    };

    private Visual CardContent(string? title, MarkdownControl body) => title is null
        ? body
        : new VStack(new TextBlock(title).Style(_styles.CardTitle), body).HorizontalAlignment(Align.Stretch);

    /// <summary>Columns between the screen edge and a frame's outer edge, so its border falls in
    /// the transcript's gutter column.</summary>
    private int Gutter(TileSet tiles) => Math.Max(0, _styles.TranscriptGutterCols - tiles.BorderCol);

    /// <summary>A tool (hands) line: a one-row chip on the gutter, cut short with an ellipsis when
    /// it is wider than the transcript.</summary>
    private DocumentFlowItem ToolItem(string text) => new()
    {
        Content = new FlowDocument().Add(PillOf(text, _styles.Tool, _tiles!.Tool)),
        Alignment = DocumentFlowAlignment.Left,
        MaxWidthPercent = 100,
        Padding = new Thickness(_styles.TranscriptGutterCols, 0, 1, 0),
    };

    private static DocumentFlowItem LineItem(string text, Style style) => new()
    {
        Content = new FlowDocument().Add(Text(text, style)),
        Alignment = DocumentFlowAlignment.Left,
        Padding = new Thickness(1, 0, 1, 0),
    };

    /// <summary>One row of text on its fill between the set's cap tiles.</summary>
    private static TileFrame PillOf(string text, Pill pill, TileSet caps) => new(
        new TextBlock(text) { Trimming = TextTrimming.EndEllipsis }.Style(pill.Text), caps, pill.Fill, Align.Start);

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

    private Visual BuildHeader(ChatHeader header) => new Header()
        .Left(new Padder(new HStack(
                new TextBlock("forge").Style(_styles.Brand),
                new TextBlock("│ PROJECT").Style(_styles.Label),
                new TextBlock(header.Project).Style(_styles.Project))
            .Spacing(1)).Padding(new Thickness(1, 0, 0, 0)))
        .Right(new Padder(new HStack(
                new TextBlock($"{header.Mission.ToUpperInvariant()} · V{header.Version} · ").Style(_styles.Label),
                _approvedSlot,
                new TextBlock($" · {header.Profile}").Style(_styles.Label))).Padding(new Thickness(0, 0, 1, 0)))
        .Style(_styles.Header);

    private PromptEditor BuildComposer(ChatHeader header)
    {
        // Shift+Enter is the only newline gesture: the editor attaches before the kitty keyboard
        // probe completes, and the default fallback would otherwise rebind newline to Ctrl+N.
        var composer = new PromptEditor(PromptEditorConfig.Default with { InsertNewLineFallbackGesture = null })
            .PromptMarkup("›")
            .Placeholder($"Message {header.Mission} v{header.Version}…")
            .AutoSizeMode(TextEditorAutoSizeMode.Height)
            .MinHeight(1)
            .MaxHeight(6)
            .Style(_styles.Composer);
        // Ctrl-C belongs to the screen (stop the run); a mouse selection is still copied by the app.
        composer.RemoveCommand("TextEditor.Copy");
        return composer;
    }

    private Rule Divider() => new Rule().Style(_styles.Divider);
}
