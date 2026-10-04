using System.Reflection;
using System.Collections.Concurrent;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Rendering;

namespace ForgeMission.Tests.Cli;

// forge chat start page (Phase 60): a real ChatTui opens on the page (Chat selected, the key bar
// kept); Enter or a click on Chat swaps in the chat; Create does nothing. Keys and clicks go
// through a real XenoAtom app in a virtual terminal. Read through reflection like the other CLI tests.
[Collection(XenoAtomUiCollection.Name)]
public sealed class StartPageTests
{
    private static readonly Assembly Forge = LoadForge();
    private const string Title = "Where do you want to start?";

    [Fact]
    public void The_TUI_opens_on_the_page_with_Chat_selected()
    {
        var tui = NewTui();
        var text = RenderText(tui.Root);
        Assert.Contains(Title, text);
        Assert.Contains("Write and evaluate a reusable mission version, then approve it for use.", text);
        Assert.Contains("Start a durable chat on an approved mission version. It stays pinned to that version.", text);
        Assert.Contains("newline", text);
        Assert.Equal(1, Get<int>(tui.List, "SelectedIndex"));
    }

    [Fact]
    public async Task Enter_opens_the_chat() =>
        Assert.Equal(new[] { false }, await Run([Key(TerminalKey.Enter)]));

    [Fact]
    public async Task Create_does_nothing_and_a_click_on_Chat_opens_the_chat()
    {
        var rows = RenderText(NewTui().Root).Split('\n');
        var chat = Array.FindIndex(rows, line => line.Contains("Chat with a mission"));
        Assert.Equal(new[] { true, false }, await Run([Key(TerminalKey.Up), Key(TerminalKey.Enter)], Click(chat)));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs a new TUI's screen in a virtual terminal with the start rows focused and sends
    /// each phase of events ten ticks after the last; after each phase, whether the page is still
    /// where the transcript goes.</summary>
    private static async Task<bool[]> Run(params TerminalEvent[][] phases)
    {
        const int TicksPerPhase = 10;
        var tui = NewTui();
        var backend = new VirtualTerminalBackend(initialSize: new TerminalSize(100, 30));
        using var terminal = Terminal.Open(backend, force: true);
        var shows = new List<bool>();
        var ticks = 0;

        ValueTask<TerminalLoopResult> Update(TerminalRunningContext context)
        {
            if (ticks == 0) context.App.Focus(tui.List);
            if (ticks % TicksPerPhase == 0)
            {
                if (ticks > 0) shows.Add(tui.PageShows());
                if (ticks / TicksPerPhase < phases.Length)
                    foreach (var item in phases[ticks / TicksPerPhase]) backend.PushEvent(item);
            }
            ticks++;
            return ValueTask.FromResult(shows.Count == phases.Length ? TerminalLoopResult.Stop : TerminalLoopResult.Continue);
        }

        await Task.Run(async () => await Terminal.RunAsync(tui.Root, Update, new TerminalRunOptions())).WaitAsync(TimeSpan.FromSeconds(10));
        return [.. shows];
    }

    /// <summary>A ChatTui as RunAsync builds it, with its screen's images in place (as on the first tick).</summary>
    private static (Visual Root, Visual List, Func<bool> PageShows) NewTui()
    {
        var tuiType = Type("ForgeMission.Cli.Tui.ChatTui");
        var theme = Type("ForgeMission.Cli.Tui.ForgeTheme").GetProperty("Dark", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        var header = Activator.CreateInstance(Type("ForgeMission.Cli.Tui.ChatHeader"), "chat", "Chat", 1, "anthropic", "ameer")!;
        var fonts = Type("ForgeMission.Cli.Tui.Graphics.TextFonts").GetMethod("LoadEmbedded")!.Invoke(null, null)!;
        var tui = Activator.CreateInstance(tuiType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [null, Guid.NewGuid(), header, theme, fonts, null, new ConcurrentQueue<string>(), new CancellationTokenSource()], null)!;
        var styles = Field(tui, "_styles");
        var screen = Field(tui, "_screen");
        var cell = Activator.CreateInstance(Type("ForgeMission.Cli.Tui.Graphics.CellSize"), 19, 42)!;
        var tiles = Type("ForgeMission.Cli.Tui.ScreenTiles").GetMethod("Create")!.Invoke(null, [styles, cell]);
        screen.GetType().GetMethod("UseImages")!.Invoke(screen, [tiles, ChatScreenTileTests.TextImagesFor(styles, cell)]);
        var start = Field(tui, "_start");
        var view = Get<Visual>(start, "View");
        var dock = Field(screen, "_dock");
        return (Get<Visual>(screen, "Root"), Get<Visual>(start, "List"), () => ReferenceEquals(Get<Visual>(dock, "Content"), view));
    }

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static T Get<T>(object target, string property) => (T)target.GetType().GetProperty(property)!.GetValue(target)!;

    private static TerminalKeyEvent Key(TerminalKey key) => new() { Key = key };

    private static TerminalEvent[] Click(int row) =>
    [
        new TerminalMouseEvent { Kind = TerminalMouseKind.Down, Button = TerminalMouseButton.Left, X = 10, Y = row },
        new TerminalMouseEvent { Kind = TerminalMouseKind.Up, Button = TerminalMouseButton.Left, X = 10, Y = row },
    ];

    private static string RenderText(Visual root) =>
        string.Join("\n", VisualSnapshotRenderer.Render(root, 100, 30).ToMarkupLines().Select(Plain));

    /// <summary>A snapshot markup line without its style tags.</summary>
    private static string Plain(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(markup, @"\[(?!\[)[^\]]*\]", "").Replace("[[", "[").Replace("]]", "]");

    private static Type Type(string name) => Forge.GetType(name, throwOnError: true)!;

    private static Assembly LoadForge()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ForgeMission.Cli", "bin", "Debug", "net10.0", "forge.dll");
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate built forge.dll for CLI reflection tests.");
    }
}
