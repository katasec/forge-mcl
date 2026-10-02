using System.Diagnostics;
using System.Reflection;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;
using static ForgeMission.Tests.Cli.ForgeText;

namespace ForgeMission.Tests.Cli;

// Phase 56 Task 5 (supervisor's live check): the motion on the real ChatScreen inside a running
// TerminalApp loop (VirtualTerminalBackend) with the real clock — what the off-screen tests with a
// test clock cannot show. A pending card, then a live delta: within 300 ms the streamed text has its
// final colour (the fade's frames are driven by the app's animation clock). Then the turn ends: the
// spinner and "replying" go. A recorder drawn over the screen keeps every cell the app renders.
[Collection(XenoAtomUiCollection.Name)]
public sealed class ChatScreenLiveMotionTests
{
    private const uint SpinnerBit = 1u << 22, TextBit = 1u << 23;
    private static readonly Type ScreenType = Type("ForgeMission.Cli.Tui.ChatScreen");
    private static readonly Type CellsType = Type("ForgeMission.Cli.Tui.XenoCells");

    [Fact]
    public async Task A_live_delta_shows_its_final_colours_within_300_ms_and_the_turn_end_clears_the_spinner()
    {
        var backend = new VirtualTerminalBackend(initialSize: new TerminalSize(100, 40));
        using var terminal = Terminal.Open(backend, force: true);
        var styles = Styles("Light");
        var header = Activator.CreateInstance(Type("ForgeMission.Cli.Tui.ChatHeader"), "chat", "Chat", 1, "anthropic", "ameer")!;
        var screen = Activator.CreateInstance(ScreenType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [header, styles, (Func<long>)Stopwatch.GetTimestamp, (Action<string>)(_ => { })], null)!;
        var recorder = new Recorder(100, 40);
        var root = new ZStack((Visual)ScreenType.GetProperty("Root")!.GetValue(screen)!, recorder);
        var strong = Token("TextStrong");
        var step = 0;
        var deltaAt = 0L;
        var endAt = 0L;
        Color? textAt300 = null;
        string? afterEnd = null;
        var spinnerWhileStreaming = false;
        var spinnerAfterEnd = true;

        ValueTask<TerminalLoopResult> Update(TerminalRunningContext context)
        {
            var now = Stopwatch.GetTimestamp();
            switch (step)
            {
                case 0:
                    var cell = Cell(19, 42);
                    var tiles = Type("ForgeMission.Cli.Tui.ScreenTiles").GetMethod("Create")!.Invoke(null, [styles, cell]);
                    ScreenType.GetMethod("UseImages")!.Invoke(screen, [tiles, ChatScreenTileTests.TextImagesFor(styles, cell)]);
                    Show(screen, You("hi"), Card(null, false));
                    step = 1;
                    deltaAt = now + Ms(200);
                    break;
                case 1 when now >= deltaAt:
                    Show(screen, You("hi"), Card("Hello world", true));
                    deltaAt = now;
                    step = 2;
                    break;
                case 2 when now >= deltaAt + Ms(300):
                    textAt300 = recorder.Foreground("Hello world");
                    spinnerWhileStreaming = recorder.HasSpinner();
                    Show(screen, You("hi"), Card("Hello world", false));
                    endAt = now;
                    step = 3;
                    break;
                case 3 when now >= endAt + Ms(200):
                    afterEnd = recorder.Screen();
                    spinnerAfterEnd = recorder.HasSpinner();
                    return ValueTask.FromResult(TerminalLoopResult.Stop);
            }
            return ValueTask.FromResult(now > deltaAt + Ms(5000) && step < 3 ? TerminalLoopResult.Stop : TerminalLoopResult.Continue);
        }

        await Task.Run(async () => await Terminal.RunAsync(root, Update, new TerminalRunOptions())).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(strong, textAt300);
        Assert.True(spinnerWhileStreaming, "the progress spinner should turn while the reply streams");
        Assert.False(spinnerAfterEnd, "the spinner should be gone once the turn ended");
        Assert.DoesNotContain("replying", afterEnd);
        Assert.Contains("Hello world", afterEnd);
    }

    private static void Show(object screen, params object[] blocks)
    {
        var array = Array.CreateInstance(Type("ForgeMission.Cli.Tui.TranscriptBlock"), blocks.Length);
        for (var i = 0; i < blocks.Length; i++) array.SetValue(blocks[i], i);
        ScreenType.GetMethod("Show")!.Invoke(screen, [array]);
    }

    private static object Card(string? text, bool streaming) =>
        Activator.CreateInstance(Type("ForgeMission.Cli.Tui.ParticipantCard"), "Answerer", text, "Chat", streaming)!;

    private static object You(string text) => Activator.CreateInstance(Type("ForgeMission.Cli.Tui.YouBlock"), text)!;

    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    private static Color Token(string name) => (Color)Type("ForgeMission.Cli.Tui.ForgeTheme").GetProperty(name)!.GetValue(Theme("Light"))!;

    /// <summary>Drawn last over the whole screen: copies every cell the app renders (inside each
    /// frame's clip) into its own grid.</summary>
    private sealed class Recorder : Visual
    {
        private readonly (string Glyph, Color? Fg, bool Placeholder)[,] _cells;

        public Recorder(int width, int height)
        {
            _cells = new (string, Color?, bool)[height, width];
            IsHitTestVisible = false;
            HorizontalAlignment = Align.Stretch;
            VerticalAlignment = Align.Stretch;
        }

        protected override SizeHints MeasureCore(in LayoutConstraints constraints) => SizeHints.Fixed(new Size(0, 0));

        protected override void RenderOverride(CellBuffer buffer)
        {
            var clip = (Rectangle)CellsType.GetMethod("Clip")!.Invoke(null, [buffer])!;
            for (var y = clip.Y; y < clip.Y + clip.Height && y < _cells.GetLength(0); y++)
            for (var x = clip.X; x < clip.X + clip.Width && x < _cells.GetLength(1); x++)
            {
                var cell = CellsType.GetMethod("Read")!.Invoke(null, [buffer, x, y])!;
                var glyph = (string)CellsType.GetMethod("Glyph")!.Invoke(null, [cell])!;
                var style = Get<Style>(cell, "Style");
                _cells[y, x] = (glyph, style.TryGetForeground(out var c) ? c : null, Get<bool>(cell, "Placeholder"));
            }
        }

        public string Screen() => string.Join('\n', Enumerable.Range(0, _cells.GetLength(0)).Select(Row));

        public Color? Foreground(string text)
        {
            for (var y = 0; y < _cells.GetLength(0); y++)
            {
                var x = Row(y).IndexOf(text, StringComparison.Ordinal);
                if (x >= 0) return _cells[y, x].Fg;
            }
            return null;
        }

        public bool HasSpinner()
        {
            foreach (var cell in _cells)
                if (cell.Placeholder && cell.Fg is { } c && ((uint)(c.R << 16 | c.G << 8 | c.B) & (SpinnerBit | TextBit)) == SpinnerBit)
                    return true;
            return false;
        }

        private string Row(int y) =>
            string.Concat(Enumerable.Range(0, _cells.GetLength(1)).Select(x => _cells[y, x].Placeholder ? "▒" : _cells[y, x].Glyph ?? " "));
    }
}
