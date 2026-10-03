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
// Motion (Task 5): each reply body carries three overlays — its fade-in (FadeIn), its streaming
// caret (StreamCaret) and its link probe (LinkPointer) — and a card's frame darkens its edge on
// hover. The progress row and a running tool chip show a spinner (SpinnerCells) only while a reply
// is in flight, so an idle screen has nothing animating.
// /edit (spike): ShowEditor puts a FileEditor's view where the transcript is, hides the progress row
// and the composer, and turns the key bar into the editor's keys; ShowChat puts the chat back.
internal sealed class ChatScreen
{
    private const string YouLabel = "You";
    private const string SendGlyph = "↵";
    private static readonly (string Chip, string Label)[] ChatKeys =
        [("enter", "send"), ("⇧ enter", "newline"), ("pgup/pgdn", "scroll"), ("ctrl c", "stop"), ("ctrl d", "quit")];
    private static readonly (string Chip, string Label)[] EditorKeys = [("ctrl s", "save"), ("esc", "close")];

    private readonly DocumentFlow _flow = new DocumentFlow().ItemSpacing(0);
    private readonly State<string> _progress = new("");
    // The blocks on screen, and per block what changes in place: a card's body and its overlays,
    // a running tool chip's spinner.
    private readonly List<TranscriptBlock> _shown = [];
    private readonly List<BlockView> _views = [];
    private readonly Padder _progressSpinner = new();
    private readonly Func<long> _clock;
    private readonly ForgeStyles _styles;
    private readonly ChatHeader _header;
    // Filled by UseImages: the brand and breadcrumb, the APPROVED pill, the composer's frame, the key bar.
    private readonly Padder _brandSlot = new();
    private readonly Padder _approvedSlot = new();
    private readonly Padder _composerSlot = new() { HorizontalAlignment = Align.Stretch };
    private readonly Padder _keysSlot = new();
    private readonly DockLayout _dock;
    private readonly Visual _transcript;
    private readonly Visual _progressRow;
    private ForgeCodeBlockRenderer? _codeBlocks;
    private ScreenTiles? _tiles;
    private TextImages? _text;

    public ChatScreen(ChatHeader header, ForgeStyles styles)
        : this(header, styles, Motion.Now, RawStdout.Write)
    {
    }

    /// <summary>The screen with its motion clock and pointer-shape writer given (tests).</summary>
    internal ChatScreen(ChatHeader header, ForgeStyles styles, Func<long> clock, Action<string> writePointer)
    {
        _styles = styles;
        _clock = clock;
        _header = header;
        Composer = BuildComposer(header);
        // Progress row, composer ring, one blank row, key bar; all on the transcript's gutter.
        var gutter = styles.TranscriptGutterCols;
        _transcript = _flow.Style(styles.Scroll);
        _progressRow = new HStack(_progressSpinner, new TextBlock(() => _progress.Value).Style(styles.Progress)).Margin(new Thickness(gutter, 0, 1, 0));
        _dock = new DockLayout()
            .Top(new VStack(BuildHeader(header), Divider()))
            .Content(_transcript)
            .Bottom(new VStack(
                _progressRow,
                _composerSlot,
                _keysSlot.Margin(new Thickness(gutter, styles.KeyBarGapRows, 1, 0))));
        Root = _dock;
        Root.Style(styles.Screen);
        Root.Style(styles.Markdown);
        Links = new LinkPointer(Root, writePointer);
    }

    public Visual Root { get; }

    /// <summary>The pointer's shape over links (ChatTui feeds it the pointer's moves).</summary>
    public LinkPointer Links { get; }

    /// <summary>Whether a turn from any window runs (ChatTui): a running tool chip spins only then
    /// or while a reply is pending or streams.</summary>
    public bool TurnRunning { get; set; }

    public PromptEditor Composer { get; }

    /// <summary>Whether the file editor is on screen in place of the transcript (/edit).</summary>
    public bool Editing { get; private set; }

    /// <summary>Shows a view in place of the transcript, with the progress row and the composer
    /// hidden. A file editor (<paramref name="editor"/>) also sets Editing and shows the editor's
    /// keys, which needs the images (HasTiles); the start page (Phase 60) keeps the key bar.</summary>
    public void ShowEditor(Visual view, bool editor = true)
    {
        Editing = editor;
        _dock.Content = view;
        _progressRow.IsVisible = false;
        _composerSlot.IsVisible = false;
        if (editor) _keysSlot.Content = KeyBar(EditorKeys);
    }

    /// <summary>Puts the transcript, progress row, composer and chat keys back.</summary>
    public void ShowChat()
    {
        Editing = false;
        _dock.Content = _transcript;
        _progressRow.IsVisible = true;
        _composerSlot.IsVisible = true;
        _keysSlot.Content = KeyBar(ChatKeys);
    }

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
        _keysSlot.Content = KeyBar(ChatKeys);
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
            if (_views[i].Card is { } gone) Links.Forget(gone.Probe);
            _views.RemoveAt(i);
            _shown.RemoveAt(i);
        }

        for (var i = kept; i < blocks.Count; i++)
        {
            _views.Add(AppendBlock(blocks[i]));
            _shown.Add(blocks[i]);
        }

        for (var i = 0; i < blocks.Count; i++)
            UpdateCard(_views[i], blocks[i], last: i == blocks.Count - 1);

        var replying = Transcript.Replying(blocks) ?? Transcript.Streaming(blocks);
        _progress.Value = replying switch
        {
            null => "",
            "" => "replying …",
            var expert => $"{expert} is replying …",
        };
        if (_tiles is not { } tiles) return;
        _progressSpinner.Content = Spin(_progressSpinner, replying is not null, tiles.ProgressSpinner, _styles.SurfaceFill);
        UpdateChips(tiles, replying is not null || TurnRunning);
    }

    /// <summary>A card's text and motion. Live reply deltas (53.8) call this several times a second:
    /// SetMarkdown assigns only a card whose text changed, and an unchanged card carries the same
    /// string instance, so every other card costs one reference comparison and is never re-parsed.
    /// A changed streaming card fades in what changed; the caret blinks while the card is pending
    /// (the last card, no text yet) or streams.</summary>
    private static void UpdateCard(BlockView view, TranscriptBlock block, bool last)
    {
        if (view.Card is not { } motion) return;
        if (block is not ParticipantCard card)
        {
            motion.Caret.Active = true;     // the pending reply before any participant starts
            return;
        }
        var changed = SetMarkdown(view.Body!, card.Text ?? "", card.Streaming);
        if (changed && card.Streaming) motion.Fade.MarkDelta();
        if (!card.Streaming) motion.Fade.Settle();
        motion.Caret.Active = card.Streaming || (card.Text is null && last);
    }

    /// <summary>Running tool chips spin while a reply is in flight; otherwise they show their text
    /// with its trailing ellipsis, as a finished chip shows its outcome.</summary>
    private void UpdateChips(ScreenTiles tiles, bool inFlight)
    {
        foreach (var view in _views)
        {
            if (view.Chip is not { } chip) continue;
            chip.Spinner.Content = Spin(chip.Spinner, inFlight, tiles.ToolSpinner, _styles.Tool.Fill);
            chip.Spinning.Value = inFlight;
        }
    }

    /// <summary>The spinner a slot shows: the one it has while it should spin, a new one when it
    /// starts, none when it stops.</summary>
    private Visual? Spin(Padder slot, bool spinning, SpinnerFrames frames, Style fill) =>
        !spinning ? null : slot.Content as SpinnerCells ?? new SpinnerCells(frames, fill, _clock);

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

    private BlockView AppendBlock(TranscriptBlock block)
    {
        switch (block)
        {
            case YouBlock you:
                _flow.Items.Add(YouItem(you.Text, you.Sent));
                return BlockView.None;
            case PendingYouBlock pending:
                _flow.Items.Add(YouItem(pending.Text, pending.Sent));
                return BlockView.None;
            case PendingReplyBlock:
                return AppendCard(null);
            case ParticipantCard card:
                return AppendCard(card);
            case NoticeLine notice:
                _flow.Items.Add(LineItem(notice.Text, _styles.Notice));
                return BlockView.None;
            case ErrorLine error:
                _flow.Items.Add(LineItem(error.Text, _styles.ErrorNotice));
                return BlockView.None;
            case HandsLine { Outcome: null } running:
                return AppendRunningTool(running);
            case HandsLine hands:
                _flow.Items.Add(ToolItem(PillOf(Transcript.HandsText(hands), _styles.Tool, _tiles!.Tool)));
                return BlockView.None;
            default:
                throw new InvalidOperationException($"No view for {block.GetType().Name}.");
        }
    }

    /// <summary>A reply card (<paramref name="card"/> null while pending) with its body's overlays.</summary>
    private BlockView AppendCard(ParticipantCard? card)
    {
        var body = CardBody();
        var motion = new CardMotion(new FadeIn(_clock), new StreamCaret(_styles.StreamCaret, _clock), Links.NewProbe());
        var layers = new ZStack(body, motion.Fade, motion.Probe, motion.Caret).HorizontalAlignment(Align.Stretch);
        _flow.Items.Add(CardItem(card, layers));
        return new BlockView(body, motion, null);
    }

    /// <summary>A running tool chip: its spinner slot, then its label (with the ellipsis when it
    /// does not spin; TUI only — the line mode keeps HandsText).</summary>
    private BlockView AppendRunningTool(HandsLine line)
    {
        var chip = new ChipMotion(new Padder(), new State<bool>(false));
        var label = new TextBlock(() => chip.Spinning.Value ? line.Label : Transcript.HandsText(line))
            { Trimming = TextTrimming.EndEllipsis }.Style(_styles.Tool.Text);
        _flow.Items.Add(ToolItem(new TileFrame(new HStack(chip.Spinner, label), _tiles!.Tool, _styles.Tool.Fill, Align.Start)));
        return new BlockView(null, null, chip);
    }

    /// <summary>A message that fits one row is a capped pill; one that wraps or has line breaks is
    /// the user ring. The frame chooses at layout, so a window resize can switch it. The label and
    /// the user's avatar sit in a column kept free beside the frame, centred on it, so a wrapping
    /// message never covers them. The message is at most UserMaxWidthPercent of the transcript wide
    /// and never left of the gutter. The label carries the time it was sent (Phase 59).</summary>
    private DocumentFlowItem YouItem(string text, DateTimeOffset sent)
    {
        var label = $"{YouLabel} · {Transcript.TimeOf(sent)}";
        var avatar = Image(TextKind.UserAvatar, Initial(_header.User), _styles.SurfaceFill);
        var side = new HStack(new TextBlock(label).Style(_styles.YouLabel), avatar).Spacing(_styles.AvatarGapCols);
        return new DocumentFlowItem
        {
            Content = new FlowDocument().Add(new ZStack(
                    new Padder(TileFrame.OneLineOr(Text(text, _styles.UserText), _tiles!.UserCaps, _tiles.UserRing, _styles.UserFill, Align.End))
                    {
                        Padding = new Thickness(0, 0, label.Length + _styles.AvatarGapCols + avatar.Image.Cols + 1, 0),
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
    private static bool SetMarkdown(MarkdownControl body, string markdown, bool streaming)
    {
        var pipeline = ForgeMarkdown.For(streaming, markdown);
        if (body.Pipeline != pipeline) body.Pipeline = pipeline;
        if (body.Markdown == markdown) return false;
        body.Markdown = markdown;
        return true;
    }

    /// <summary>A reply card; <paramref name="card"/> is null while no participant has started.
    /// The whole card is one block (its frame, with the hover edge), after a blank gap row, inset
    /// by the gutter so the frame's border falls in the transcript's gutter column.</summary>
    private DocumentFlowItem CardItem(ParticipantCard? card, Visual body) => new()
    {
        Content = new FlowDocument().Add(TileFrame.WithHover(CardContent(card, body), _tiles!.Card, _tiles.CardHover, _styles.CardFill, Align.Stretch)),
        Alignment = DocumentFlowAlignment.Left,
        MaxWidthPercent = 100,
        Padding = new Thickness(Gutter(_tiles.Card), _styles.CardGapRows, Gutter(_tiles.Card), 0),
    };

    private Visual CardContent(ParticipantCard? card, Visual body) => card is null
        ? body
        : new VStack(CardHead(card), body).HorizontalAlignment(Align.Stretch);

    /// <summary>The participant's avatar and name, as images, and the time the reply started (Phase
    /// 59) as muted text; a name an image cannot carry stays bold terminal text (G9).</summary>
    private HStack CardHead(ParticipantCard card)
    {
        var title = card.Title;
        Visual name = TextArt.Allows(title)
            ? Image(TextKind.Name, title, _styles.CardFill)
            : new TextBlock(title).Style(_styles.FallbackStrong);
        var time = new TextBlock($"· {Transcript.TimeOf(card.Sent)}").Style(_styles.YouLabel).VerticalAlignment(Align.Center);
        return new HStack(Image(TextKind.Avatar, Initial(title), _styles.CardFill), name, time).Spacing(_styles.AvatarGapCols);
    }

    /// <summary>Columns between the screen edge and a frame's outer edge, so its border falls in
    /// the transcript's gutter column.</summary>
    private int Gutter(TileSet tiles) => Math.Max(0, _styles.TranscriptGutterCols - tiles.BorderCol);

    /// <summary>A tool (hands) line: a one-row chip on the gutter, cut short with an ellipsis when
    /// it is wider than the transcript.</summary>
    private DocumentFlowItem ToolItem(TileFrame chip) => new()
    {
        Content = new FlowDocument().Add(chip),
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
    private HStack KeyBar((string Chip, string Label)[] keys) => new HStack([.. keys.Select(key => (Visual)new HStack(
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

/// <summary>What one shown block changes in place: a card's body and overlays, a running chip's spinner.</summary>
internal sealed record BlockView(MarkdownControl? Body, CardMotion? Card, ChipMotion? Chip)
{
    public static readonly BlockView None = new(null, null, null);
}

/// <summary>A reply body's overlays (Task 5).</summary>
internal sealed record CardMotion(FadeIn Fade, StreamCaret Caret, Visual Probe);

/// <summary>A running tool chip's spinner slot, and whether it spins.</summary>
internal sealed record ChipMotion(Padder Spinner, State<bool> Spinning);
