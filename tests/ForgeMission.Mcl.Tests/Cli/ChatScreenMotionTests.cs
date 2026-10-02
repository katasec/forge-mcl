using System.Diagnostics;
using System.Reflection;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Animation;
using XenoAtom.Terminal.UI.Rendering;
using static ForgeMission.Tests.Cli.ForgeText;

namespace ForgeMission.Tests.Cli;

// Phase 56 Task 5: motion on the real ChatScreen, rendered off screen at 19×42 with a test clock.
// A streamed delta fades in only the cells it changed, each on its own clock; image placeholders
// are never restyled; the caret blinks after the text while the reply streams; the progress row and
// a running tool chip spin only while a reply is in flight; a hovered card paints its hover edge;
// link cells give the hand pointer; and an idle screen asks for no animation frames.
[Collection(XenoAtomUiCollection.Name)]
public sealed class ChatScreenMotionTests
{
    private const int Width = 80;
    private const uint SpinnerBit = 1u << 22, TextBit = 1u << 23;
    private static readonly Type ScreenType = Type("ForgeMission.Cli.Tui.ChatScreen");
    private static readonly Type CellsType = Type("ForgeMission.Cli.Tui.XenoCells");
    private static readonly Color Surface = Token("CardSurface"), Strong = Token("TextStrong");

    [Fact]
    public void A_delta_fades_only_the_cells_it_changed()
    {
        var screen = new Screen();
        screen.Show(Card("Hello", streaming: true)).Render();
        screen.Clock += Ms(300);

        screen.Show(Card("Hello world", streaming: true));
        var start = screen.Render();
        Assert.Equal(Strong, Foreground(start, "Hello"));
        Assert.Equal(Surface, Foreground(start, "world"));

        screen.Clock += Ms(110);
        var middle = Foreground(screen.Render(), "world");
        Assert.NotEqual(Surface, middle);
        Assert.NotEqual(Strong, middle);

        screen.Clock += Ms(110);
        Assert.Equal(Strong, Foreground(screen.Render(), "world"));
    }

    [Fact]
    public void Overlapping_deltas_keep_each_cells_own_fade()
    {
        var screen = new Screen();
        var t0 = screen.Clock;
        screen.Show(Card("Hello", streaming: true)).Render();
        screen.Clock = t0 + Ms(100);
        screen.Show(Card("Hello big", streaming: true)).Render();

        screen.Clock = t0 + Ms(150);
        var frame = screen.Render();

        Assert.Equal(Mix(Surface, Strong, Progress(t0, t0 + Ms(150))), Foreground(frame, "Hello"));
        Assert.Equal(Mix(Surface, Strong, Progress(t0 + Ms(100), t0 + Ms(150))), Foreground(frame, "big"));
    }

    [Fact]
    public void Placeholder_cells_are_never_restyled_and_code_beside_ring_tiles_fades_from_its_own_fill()
    {
        const string reply = "## Heading\n\nSome text\n\n```\ncode\n```\n";
        var fading = new Screen();
        var during = fading.Show(Card(reply, streaming: true)).Render();
        var settled = new Screen();
        var after = settled.Show(Card(reply, streaming: false)).Render();

        var placeholders = 0;
        for (var y = 0; y < during.Height; y++)
        for (var x = 0; x < during.Width; x++)
        {
            if (!IsPlaceholder(during, x, y) || (Id(during, x, y) & SpinnerBit) != 0) continue;
            placeholders++;
            Assert.Equal(Style(after, x, y), Style(during, x, y));
        }
        Assert.True(placeholders > 0, "expected heading and code-block image cells");
        var (cx, cy) = Find(during, "code");
        Assert.True(IsPlaceholder(during, 3, cy) || Row(during, cy).Contains('▒'), "code sits between ring tiles");
        Style(during, cx, cy).TryGetBackground(out var codeFill);
        Assert.Equal(codeFill, Foreground(during, "code"));
    }

    [Fact]
    public void The_caret_blinks_after_the_last_text_cell_while_streaming_and_is_gone_after()
    {
        var screen = new Screen();
        var frame = screen.Show(Card("Hello", streaming: true)).Render();
        var (x, y) = Find(frame, "Hello");
        Assert.Equal("█", Glyph(frame, x + 5, y));
        Assert.Equal(Token("Accent"), Foreground(frame, x + 5, y));

        screen.Clock += Ms(600);
        Assert.NotEqual("█", Glyph(screen.Render(), x + 5, y));
        screen.Clock += Ms(500);
        Assert.Equal("█", Glyph(screen.Render(), x + 5, y));

        var ended = screen.Show(Card("Hello", streaming: false)).Render();
        Assert.DoesNotContain("█", All(ended));
    }

    [Fact]
    public void A_pending_reply_shows_the_caret_in_its_first_body_cell()
    {
        var frame = new Screen().Show(You("hi"), Pending()).Render();

        Assert.Contains("█", All(frame));
        Assert.DoesNotContain("▌", All(frame));
    }

    [Fact]
    public void The_progress_spinner_turns_while_a_reply_streams_and_is_gone_when_it_ends()
    {
        var screen = new Screen();
        var streaming = screen.Show(Card("Hi", streaming: true)).Render();
        Assert.Contains("Answerer is replying …", All(streaming));
        var first = SpinnerIds(streaming);
        Assert.NotEmpty(first);

        screen.Clock += Ms(80);
        Assert.NotEqual(first, SpinnerIds(screen.Render()));

        Assert.Empty(SpinnerIds(screen.Show(Card("Hi", streaming: false)).Render()));
        // A bound text needs a running app to refresh, so the ended row is read from a new screen.
        var ended = new Screen().Show(Card("Hi", streaming: false)).Render();
        Assert.Empty(SpinnerIds(ended));
        Assert.DoesNotContain("replying", All(ended));
    }

    [Fact]
    public void A_running_tool_chip_spins_in_place_of_its_ellipsis_only_while_a_reply_is_in_flight()
    {
        var screen = new Screen();
        var running = screen.Show(You("read it"), Hands("Read notes.txt", null), Pending()).Render();
        var row = Find(running, "Read notes.txt").Y;
        Assert.Contains(SpinnerIdsOnRow(running, row), id => id % 20 >= 10);   // the tool spinner (index 10..19)
        Assert.DoesNotContain("Read notes.txt …", Row(running, row));

        // The turn ended without an outcome: the chip stops spinning and keeps its ellipsis. (A bound
        // text needs a running app to refresh, so the stopped chip is read from a new screen.)
        Assert.Empty(SpinnerIdsOnRow(screen.Show(You("read it"), Hands("Read notes.txt", null)).Render(), row));
        var stopped = new Screen().Show(You("read it"), Hands("Read notes.txt", null)).Render();
        var stoppedRow = Find(stopped, "Read notes.txt").Y;
        Assert.Empty(SpinnerIdsOnRow(stopped, stoppedRow));
        Assert.Contains("Read notes.txt …", Row(stopped, stoppedRow));
    }

    [Fact]
    public void A_hovered_card_paints_its_hover_edge()
    {
        var screen = new Screen();
        screen.Show(Card("Hello", streaming: false));
        var normal = TileSets(screen.Render());
        var hover = new State<bool>(true);
        screen.CardFrame().BindIsHovered(hover);

        var hovered = TileSets(screen.Render());

        Assert.Contains(0, normal);
        Assert.DoesNotContain(7, normal);
        Assert.Contains(7, hovered);
        Assert.DoesNotContain(0, hovered);
    }

    [Fact]
    public void Link_cells_give_the_hand_and_other_cells_the_default_pointer_written_once_per_change()
    {
        var screen = new Screen();
        var frame = screen.Show(Card("see [docs](https://example.com) here", streaming: false)).Render();
        var (x, y) = Find(frame, "docs");
        var (sx, sy) = Find(frame, "see");

        screen.PointerAt(x + 1, y);
        screen.PointerAt(x + 2, y);
        screen.PointerAt(sx, sy);

        Assert.Equal(["\u001b]22;pointer\u001b\\", "\u001b]22;default\u001b\\"], screen.Written);
        Assert.True(screen.IsLink(x, y));

        screen.Show(You("gone")).Render();
        Assert.False(screen.IsLink(x, y));
    }

    [Fact]
    public void An_idle_screen_asks_for_no_animation_frames()
    {
        var screen = new Screen();
        screen.Show(Card("Hello", streaming: true)).Render();
        screen.Show(Card("Hello there", streaming: true)).Render();
        screen.Show(Card("Hello there", streaming: false)).Render();
        screen.Clock += Ms(1000);
        screen.Render();

        var animated = screen.Animated();
        Assert.NotEmpty(animated);
        foreach (var visual in animated) visual.AdvanceAnimation(screen.Clock);
        Assert.All(animated, visual => Assert.Equal(long.MaxValue, visual.NextAnimationTick));
        Assert.DoesNotContain(animated, visual => visual.GetType().Name == "SpinnerCells");
    }

    // ── The screen ──────────────────────────────────────────────────────────────────────────

    private sealed class Screen
    {
        private readonly object _screen;

        public Screen()
        {
            var styles = Styles("Light");
            var header = Activator.CreateInstance(Type("ForgeMission.Cli.Tui.ChatHeader"), "chat", "Chat", 1, "anthropic", "ameer")!;
            Func<long> clock = () => Clock;
            Action<string> write = Written.Add;
            _screen = Activator.CreateInstance(ScreenType, BindingFlags.Instance | BindingFlags.NonPublic, null, [header, styles, clock, write], null)!;
            var cell = Cell(19, 42);
            var tiles = Type("ForgeMission.Cli.Tui.ScreenTiles").GetMethod("Create")!.Invoke(null, [styles, cell]);
            ScreenType.GetMethod("UseImages")!.Invoke(_screen, [tiles, ChatScreenTileTests.TextImagesFor(styles, cell)]);
        }

        public long Clock { get; set; } = 1_000_000_000;

        public List<string> Written { get; } = [];

        private Visual Root => (Visual)ScreenType.GetProperty("Root")!.GetValue(_screen)!;

        private object Links => ScreenType.GetProperty("Links")!.GetValue(_screen)!;

        public Screen Show(params object[] blocks)
        {
            var array = Array.CreateInstance(Type("ForgeMission.Cli.Tui.TranscriptBlock"), blocks.Length);
            for (var i = 0; i < blocks.Length; i++) array.SetValue(blocks[i], i);
            ScreenType.GetMethod("Show")!.Invoke(_screen, [array]);
            return this;
        }

        public CellBuffer Render() => VisualSnapshotRenderer.Render(Root, Width, 40);

        public void PointerAt(int x, int y) => Links.GetType().GetMethod("PointerAt")!.Invoke(Links, [x, y]);

        public bool IsLink(int x, int y) =>
            (bool)Links.GetType().GetMethod("IsLink", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Links, [x, y])!;

        public Visual CardFrame() => Root.EnumerateVisualsDepthFirst().First(v => v.GetType().Name == "TileFrame" &&
            v.EnumerateVisualsDepthFirst().Any(c => c.GetType().Name == "FadeIn"));

        public List<IAnimatedVisual> Animated() => [.. Root.EnumerateVisualsDepthFirst().OfType<IAnimatedVisual>()];
    }

    // ── Blocks ──────────────────────────────────────────────────────────────────────────────

    private static object Card(string text, bool streaming) =>
        Activator.CreateInstance(Type("ForgeMission.Cli.Tui.ParticipantCard"), "Answerer", text, "Chat", streaming)!;

    private static object You(string text) => Activator.CreateInstance(Type("ForgeMission.Cli.Tui.YouBlock"), text)!;

    private static object Pending() => Activator.CreateInstance(Type("ForgeMission.Cli.Tui.PendingReplyBlock"), Guid.NewGuid())!;

    private static object Hands(string label, string? outcome) => Activator.CreateInstance(Type("ForgeMission.Cli.Tui.HandsLine"), label, outcome)!;

    // ── Cells ───────────────────────────────────────────────────────────────────────────────

    private static object Read(CellBuffer buffer, int x, int y) => CellsType.GetMethod("Read")!.Invoke(null, [buffer, x, y])!;

    private static Style Style(CellBuffer buffer, int x, int y) => Get<Style>(Read(buffer, x, y), "Style");

    private static bool IsPlaceholder(CellBuffer buffer, int x, int y) => Get<bool>(Read(buffer, x, y), "Placeholder");

    private static string Glyph(CellBuffer buffer, int x, int y) => (string)CellsType.GetMethod("Glyph")!.Invoke(null, [Read(buffer, x, y)])!;

    private static uint? Id(CellBuffer buffer, int x, int y) =>
        IsPlaceholder(buffer, x, y) && Style(buffer, x, y).TryGetForeground(out var c) ? (uint)(c.R << 16 | c.G << 8 | c.B) : null;

    /// <summary>A row as text: placeholder cells show as ▒.</summary>
    private static string Row(CellBuffer buffer, int y) =>
        string.Concat(Enumerable.Range(0, buffer.Width).Select(x => IsPlaceholder(buffer, x, y) ? "▒" : Glyph(buffer, x, y)));

    private static string All(CellBuffer buffer) => string.Join('\n', Enumerable.Range(0, buffer.Height).Select(y => Row(buffer, y)));

    private static (int X, int Y) Find(CellBuffer buffer, string text)
    {
        for (var y = 0; y < buffer.Height; y++)
        {
            var x = Row(buffer, y).IndexOf(text, StringComparison.Ordinal);
            if (x >= 0) return (x, y);
        }
        throw new Xunit.Sdk.XunitException($"'{text}' not on screen:\n{All(buffer)}");
    }

    private static Color Foreground(CellBuffer buffer, string text)
    {
        var (x, y) = Find(buffer, text);
        var colours = Enumerable.Range(x, text.Length).Select(cx => Foreground(buffer, cx, y)).Distinct().ToList();
        return Assert.Single(colours);
    }

    private static Color Foreground(CellBuffer buffer, int x, int y) => Style(buffer, x, y).TryGetForeground(out var c) ? c : default;

    private static List<uint> SpinnerIds(CellBuffer buffer) =>
        [.. Enumerable.Range(0, buffer.Height).SelectMany(y => SpinnerIdsOnRow(buffer, y))];

    private static List<uint> SpinnerIdsOnRow(CellBuffer buffer, int y) =>
        [.. Enumerable.Range(0, buffer.Width).Select(x => Id(buffer, x, y)).OfType<uint>()
            .Where(id => (id & (SpinnerBit | TextBit)) == SpinnerBit).Select(id => id & 63)];

    /// <summary>The tile sets named on screen (set number from the tile id).</summary>
    private static HashSet<int> TileSets(CellBuffer buffer) =>
        [.. Enumerable.Range(0, buffer.Height).SelectMany(y => Enumerable.Range(0, buffer.Width).Select(x => Id(buffer, x, y)))
            .OfType<uint>().Where(id => (id & (SpinnerBit | TextBit)) == 0).Select(id => (int)(id >> 3 & 7))];

    // ── Time and colour ─────────────────────────────────────────────────────────────────────

    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    private static double Progress(long start, long now) =>
        (double)Type("ForgeMission.Cli.Tui.Motion").GetMethod("FadeProgress")!.Invoke(null, [start, now])!;

    private static Color Mix(Color from, Color to, double t) =>
        (Color)Type("ForgeMission.Cli.Tui.Motion").GetMethod("Mix")!.Invoke(null, [from, to, t])!;

    private static Color Token(string name) => (Color)Type("ForgeMission.Cli.Tui.ForgeTheme").GetProperty(name)!.GetValue(Theme("Light"))!;
}
