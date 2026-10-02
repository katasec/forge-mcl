using ForgeMission.Cli.Tui.Graphics;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Text;

namespace ForgeMission.Cli.Tui;

/// <summary>What the header shows: the Project, the mission and its version, and the provider
/// profile the mission's definition pins; and the local account name the user's avatar initial
/// comes from.</summary>
internal sealed record ChatHeader(string Project, string Mission, int Version, string Profile, string User);

// forge chat TUI (53.5, 53.6): every visual on the screen — header, transcript, progress line,
// composer, key bar — laid out as the accepted mockups. It renders transcript blocks; it decides
// nothing about events (Transcript) or turns (ChatTui), and takes every style from ForgeStyles.
// Shapes (Phase 56) — cards, code blocks, user messages, the APPROVED pill, tool chips, the
// composer — are framed by image tiles (TileFrame); this file only places them, with the mockup's
// gutter and gap, and never builds pixels. It holds the tile-set registry (ScreenTiles) and the
// session's text images (TextImages: brand, breadcrumb, card names, avatars, headings, key chips,
// the send button, Task 4), which arrive on the TUI's first tick (UseImages), before any block is
// shown: ChatTui opens the conversation only after it, and Enter does nothing until HasTiles. The
// header images and pill, the composer frame and the key bar are placed into their slots then, and
// the code-block renderer is created then. Text an image cannot carry (G9) stays terminal text.
internal sealed class ChatScreen
{
    private const string PendingBody = "▌";
    private const string YouLabel = "You";
    private const string SendGlyph = "↵";
    private static readonly (string Chip, string Label)[] Keys =
        [("enter", "send"), ("⇧ enter", "newline"), ("pgup/pgdn", "scroll"), ("ctrl c", "stop"), ("ctrl d", "quit")];

    private readonly DocumentFlow _flow = new DocumentFlow().ItemSpacing(0);
    private readonly State<string> _progress = new("");
    // The blocks on screen, and per block the body of a card (null for other blocks).
    private readonly List<TranscriptBlock> _shown = [];
    private readonly List<MarkdownControl?> _cardBodies = [];
    private readonly ForgeStyles _styles;
    private readonly ChatHeader _header;
    // Filled by UseImages: the brand and breadcrumb, the APPROVED pill, the composer's frame, the key bar.
    private readonly Padder _brandSlot = new();
    private readonly Padder _approvedSlot = new();
    private readonly Padder _composerSlot = new() { HorizontalAlignment = Align.Stretch };
    private readonly Padder _keysSlot = new();
    private ForgeCodeBlockRenderer? _codeBlocks;
    private ScreenTiles? _tiles;
    private TextImages? _text;

    public ChatScreen(ChatHeader header, ForgeStyles styles)
    {
        _styles = styles;
        _header = header;
        Composer = BuildComposer(header);
        // Progress row, composer ring, one blank row, key bar; all on the transcript's gutter.
        var gutter = styles.TranscriptGutterCols;
        Root = new DockLayout()
            .Top(new VStack(BuildHeader(header), Divider()))
            .Content(_flow.Style(styles.Scroll))
            .Bottom(new VStack(
                new TextBlock(() => _progress.Value).Style(styles.Progress).Margin(new Thickness(gutter, 0, 1, 0)),
                _composerSlot,
                _keysSlot.Margin(new Thickness(gutter, styles.KeyBarGapRows, 1, 0))));
        Root.Style(styles.Screen);
        Root.Style(styles.Markdown);
    }

    public Visual Root { get; }

    public PromptEditor Composer { get; }

    /// <summary>The tile sets every shape is framed with, sent to the terminal before the first
    /// block, and the session's text images. Places the brand and breadcrumb, the header pill, the
    /// composer frame with its send button and the key bar, and creates the code-block renderer.</summary>
    public void UseImages(ScreenTiles tiles, TextImages text)
    {
        _tiles = tiles;
        _text = text;
        _codeBlocks = new ForgeCodeBlockRenderer(_styles.CodeBlock, tiles.CodeBlock, text,
            new HeadingStyle(_styles.CardFill, _styles.HeadingPending));
        _brandSlot.Content = new HStack(
            Image(TextKind.Brand, "forge", _styles.HeaderFill),
            Crumb(_header.Project, _header.Mission)).Spacing(_styles.BrandGapCols);
        _approvedSlot.Content = PillOf("APPROVED", _styles.Approved, tiles.Approved);
        _composerSlot.Padding = new Thickness(Gutter(tiles.Composer), 0, Gutter(tiles.Composer), 0);
        _composerSlot.Content = new TileFrame(ComposerWithSend(), tiles.Composer, _styles.ComposerFill, Align.Stretch);
        _keysSlot.Content = KeyBar();
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
                SetMarkdown(body, card.Text ?? (i == blocks.Count - 1 ? PendingBody : ""), card.Streaming);
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
                SetMarkdown(pendingBody, PendingBody, streaming: false);
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
    /// the user ring. The frame chooses at layout, so a window resize can switch it. The label and
    /// the user's avatar sit in a column kept free beside the frame, centred on it, so a wrapping
    /// message never covers them. The message is at most UserMaxWidthPercent of the transcript wide
    /// and never left of the gutter.</summary>
    private DocumentFlowItem YouItem(string text)
    {
        var avatar = Image(TextKind.UserAvatar, Initial(_header.User), _styles.SurfaceFill);
        var side = new HStack(new TextBlock(YouLabel).Style(_styles.YouLabel), avatar).Spacing(_styles.AvatarGapCols);
        return new DocumentFlowItem
        {
            Content = new FlowDocument().Add(new ZStack(
                    new Padder(TileFrame.OneLineOr(Text(text, _styles.UserText), _tiles!.UserCaps, _tiles.UserRing, _styles.UserFill, Align.End))
                    {
                        Padding = new Thickness(0, 0, YouLabel.Length + _styles.AvatarGapCols + avatar.Image.Cols + 1, 0),
                        HorizontalAlignment = Align.End,
                    },
                    side.HorizontalAlignment(Align.End).VerticalAlignment(Align.Center))
                .HorizontalAlignment(Align.End)),
            Alignment = DocumentFlowAlignment.Right,
            MaxWidthPercent = _styles.UserMaxWidthPercent,
            Padding = new Thickness(_styles.TranscriptGutterCols, 0, 1, 0),
        };
    }

    /// <summary>A reply body: Markdown in the theme's styles (set on the root), parsed by forge's
    /// pipeline (headings to images), code blocks and headings through the forge renderer, no
    /// scrolling of its own (the transcript scrolls).</summary>
    private MarkdownControl CardBody() => new("")
    {
        Pipeline = ForgeMarkdown.Complete,
        HorizontalAlignment = Align.Stretch,
        VerticalAlignment = Align.Start,
        HorizontalScrollEnabled = false,
        VerticalScrollEnabled = false,
        Options = MarkdownRenderOptions.Default with { WrapCodeBlocks = true, CodeBlockRenderer = _codeBlocks! },
    };

    /// <summary>Sets a body's text, and the pipeline for it: a streaming reply's last heading may
    /// still be incomplete (ForgeMarkdown.For). A changed pipeline re-renders the body.</summary>
    private static void SetMarkdown(MarkdownControl body, string markdown, bool streaming)
    {
        var pipeline = ForgeMarkdown.For(streaming, markdown);
        if (body.Pipeline != pipeline) body.Pipeline = pipeline;
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
        : new VStack(CardHead(title), body).HorizontalAlignment(Align.Stretch);

    /// <summary>The participant's avatar and name, as images; a name an image cannot carry stays
    /// bold terminal text (G9).</summary>
    private HStack CardHead(string title)
    {
        Visual name = TextArt.Allows(title)
            ? Image(TextKind.Name, title, _styles.CardFill)
            : new TextBlock(title).Style(_styles.FallbackStrong);
        return new HStack(Image(TextKind.Avatar, Initial(title), _styles.CardFill), name).Spacing(_styles.AvatarGapCols);
    }

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
        .Left(new Padder(_brandSlot).Padding(new Thickness(1, 0, 0, 0)))
        .Right(new Padder(new HStack(
                new TextBlock($"{header.Mission.ToUpperInvariant()} · V{header.Version} · ").Style(_styles.Label),
                _approvedSlot,
                new TextBlock($" · {header.Profile}").Style(_styles.Label))).Padding(new Thickness(0, 0, 1, 0)))
        .Style(_styles.Header);

    private PromptEditor BuildComposer(ChatHeader header)
    {
        // Shift+Enter is the only newline gesture: the editor attaches before the kitty keyboard
        // probe completes, and the default fallback would otherwise rebind newline to Ctrl+N.
        var composer = new ComposerEditor(PromptEditorConfig.Default with { InsertNewLineFallbackGesture = null })
            .PromptMarkup(ComposerEditor.PromptGlyph)
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

    // ── Text images (Phase 56 Task 4) ───────────────────────────────────────────────────────

    /// <summary>The cells of one text image, sent the first time it is asked for.</summary>
    private ImageCells Image(TextKind kind, string text, Style fill, int split = 0) =>
        new(_text!.Get(new TextImageRequest(kind, text, split)), fill);

    /// <summary>"project / Mission": one image with the mission strong, or terminal text when an
    /// image cannot carry it.</summary>
    private Visual Crumb(string project, string mission)
    {
        var prefix = $"{project} / ";
        return TextArt.Allows(prefix + mission)
            ? Image(TextKind.Crumb, prefix + mission, _styles.HeaderFill, prefix.Length)
            : new HStack(new TextBlock(prefix).Style(_styles.FallbackMuted), new TextBlock(mission).Style(_styles.FallbackStrong));
    }

    /// <summary>The composer with the send button on its bottom row, right of the text, which keeps
    /// the button's columns and a gap free.</summary>
    private ZStack ComposerWithSend()
    {
        var send = Image(TextKind.Send, SendGlyph, _styles.CardFill);
        return new ZStack(
                new Padder(Composer) { Padding = new Thickness(0, 0, send.Image.Cols + _styles.AvatarGapCols, 0), HorizontalAlignment = Align.Stretch },
                send.HorizontalAlignment(Align.End).VerticalAlignment(Align.End))
            .HorizontalAlignment(Align.Stretch);
    }

    /// <summary>Each key's chip and muted label; clipped at the right in a narrow window.</summary>
    private HStack KeyBar() => new HStack([.. Keys.Select(key => (Visual)new HStack(
            Image(TextKind.Chip, key.Chip, _styles.SurfaceFill),
            new TextBlock(key.Label).Style(_styles.KeyLabel)).Spacing(_styles.ChipGapCols))])
        .Spacing(_styles.KeyGroupGapCols);

    /// <summary>An avatar's initial (Ameer's ruling): the first letter, uppercased; none (an empty
    /// circle) when an image cannot carry it.</summary>
    internal static string Initial(string name)
    {
        var first = name.Length == 0 ? "" : char.ToUpperInvariant(name[0]).ToString();
        return TextArt.Allows(first) && char.IsLetterOrDigit(first[0]) ? first : "";
    }
}
