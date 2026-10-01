using System.Reflection;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Rendering;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (Phase 56 Task 2): a participant card on the real ChatScreen, rendered off screen.
// Like the mockup's card, it spans the transcript between the gutters whatever its content, and has
// one plain padding row above its bottom ring. Builds XenoAtom visuals, so it runs in the
// XenoAtom UI collection (never alongside a running TerminalApp).
[Collection(XenoAtomUiCollection.Name)]
public sealed class ChatScreenCardTests
{
    private const int Width = 80;

    /// <summary>The high surrogate of U+10EEEE, the kitty placeholder: one per tile cell.</summary>
    private const char Placeholder = '\uDBFB';

    private static readonly Assembly Forge = LoadForge();

    [Fact]
    public void A_short_reply_card_spans_the_transcript_and_has_a_padding_row_above_its_bottom_ring()
    {
        var rows = ScreenWithOneCard("ok").Select(line => (Line: line, Tiles: line.Count(c => c == Placeholder))).ToList();
        var ring = rows.Where(row => row.Tiles > 0).ToList();
        // Gutter: 4 columns minus the ring column holding the border (1) on each side.
        const int cardWidth = Width - 2 * 3;

        Assert.Equal(cardWidth, ring[0].Tiles);                       // top ring row: every cell is a tile
        Assert.Equal(cardWidth, ring[^1].Tiles);                      // bottom ring row
        var inside = ring.Where(row => row.Tiles == 8).ToList();      // 4 ring columns at each side
        Assert.Equal(4, ring.Count - inside.Count);                   // 2 top + 2 bottom ring rows
        Assert.Equal(3, inside.Count);                                // title, "ok", one padding row
        Assert.Contains("ok", inside[1].Line);
        Assert.DoesNotContain("ok", inside[2].Line);
    }

    /// <summary>The real ChatScreen (theme Light, card ring at 19×42) showing one participant card,
    /// rendered off screen; one string per row.</summary>
    private static string[] ScreenWithOneCard(string reply)
    {
        var stylesType = Type("ForgeMission.Cli.Tui.ForgeStyles");
        var light = Type("ForgeMission.Cli.Tui.ForgeTheme").GetProperty("Light", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        var styles = Activator.CreateInstance(stylesType, light)!;
        var header = Activator.CreateInstance(Type("ForgeMission.Cli.Tui.ChatHeader"), "chat", "Chat", 1, "anthropic")!;
        var screenType = Type("ForgeMission.Cli.Tui.ChatScreen");
        var screen = Activator.CreateInstance(screenType, [header, styles])!;
        var cell = Activator.CreateInstance(Type("ForgeMission.Cli.Tui.Graphics.CellSize"), 19, 42)!;
        var ring = Type("ForgeMission.Cli.Tui.Graphics.CardRing").GetMethod("Create")!
            .Invoke(null, [stylesType.GetProperty("CardEdges")!.GetValue(styles), 0, cell]);
        screenType.GetMethod("UseCards")!.Invoke(screen, [ring]);
        var blocks = Array.CreateInstance(Type("ForgeMission.Cli.Tui.TranscriptBlock"), 1);
        blocks.SetValue(Activator.CreateInstance(Type("ForgeMission.Cli.Tui.ParticipantCard"), "Chat:Answerer", reply, "Chat"), 0);
        screenType.GetMethod("Show")!.Invoke(screen, [blocks]);
        var root = (Visual)screenType.GetProperty("Root")!.GetValue(screen)!;
        return VisualSnapshotRenderer.Render(root, Width, 30).ToMarkupLines().ToArray();
    }

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
