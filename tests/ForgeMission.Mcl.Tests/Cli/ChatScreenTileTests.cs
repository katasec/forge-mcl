using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Rendering;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (Phase 56 Tasks 2 and 3): the shapes on the real ChatScreen, rendered off screen
// with the tile sets at 19×42. Every tile cell is a kitty placeholder whose foreground colour is
// its image id; the id names its set and slot (TileSet.ImageIds), so each test reads which tile
// sits in which cell. Builds XenoAtom visuals, so it runs in the XenoAtom UI collection (never
// alongside a running TerminalApp).
[Collection(XenoAtomUiCollection.Name)]
public sealed partial class ChatScreenTileTests
{
    private const int Width = 80;
    private const int Gutter = 4;

    // Set numbers (ScreenTiles order) and the cap slots.
    private const int CardSet = 0, CodeBlockSet = 1, UserRingSet = 2, ComposerSet = 3, UserCapsSet = 4, ApprovedSet = 5, ToolSet = 6;
    private const int LeftSlot = 3, RightSlot = 4;

    private static readonly Assembly Forge = LoadForge();

    [Fact]
    public void A_short_reply_card_spans_the_transcript_and_has_a_padding_row_above_its_bottom_ring()
    {
        var rows = new Screen().Show(Card("ok")).Render().Where(row => row.Any(c => c.Set == CardSet)).ToList();
        // Gutter: 4 columns minus the ring column holding the border (1) on each side.
        const int cardWidth = Width - 2 * 3;

        Assert.Equal(cardWidth, rows[0].Count(c => c.Set == CardSet));     // top ring row: every cell is a tile
        Assert.Equal(cardWidth, rows[^1].Count(c => c.Set == CardSet));    // bottom ring row
        var inside = rows.Where(row => row.Count(c => c.Set == CardSet) == 8).ToList(); // 4 ring columns at each side
        Assert.Equal(4, rows.Count - inside.Count);                        // 2 top + 2 bottom ring rows
        Assert.Equal(3, inside.Count);                                     // title, "ok", one padding row
        Assert.Contains("ok", Text(inside[1]));
        Assert.DoesNotContain("ok", Text(inside[2]));
    }

    [Fact]
    public void A_one_line_user_message_is_a_capped_pill()
    {
        var rows = new Screen().Show(You("hello there")).Render();
        var row = rows.Single(r => Text(r).Contains("hello there"));
        var start = Text(row).IndexOf("hello there", StringComparison.Ordinal);

        Assert.Equal([(UserCapsSet, LeftSlot), (UserCapsSet, LeftSlot)], Tiles(row[(start - 2)..start]));
        Assert.Equal([(UserCapsSet, RightSlot), (UserCapsSet, RightSlot)], Tiles(row[(start + 11)..(start + 13)]));
        Assert.Equal(" You", Text(row[(start + 13)..(start + 17)]));
        Assert.All(row[start..(start + 11)], c => Assert.Equal(Token("Light", "UserPillFill"), c.Background));
        Assert.DoesNotContain(rows.SelectMany(r => r), c => c.Set == UserRingSet);
    }

    [Fact]
    public void A_long_one_line_user_message_wraps_into_the_right_aligned_ring()
    {
        var text = string.Join(' ', Enumerable.Repeat("words", 12));
        var rows = new Screen().Show(You(text)).Render(40);
        var ring = rows.Where(r => r.Any(c => c.Set == UserRingSet)).ToList();

        Assert.DoesNotContain(rows.SelectMany(r => r), c => c.Set == UserCapsSet);
        Assert.True(ring.Count >= 4, $"expected top, two or more text rows and bottom; got {ring.Count}");
        Assert.All(ring[0].Where(c => c.Set == UserRingSet), c => Assert.InRange(c.Slot, 0, 2));
        Assert.All(ring[^1].Where(c => c.Set == UserRingSet), c => Assert.InRange(c.Slot, 5, 7));
        // Never left of the gutter, at most 3/4 of the transcript wide.
        Assert.All(ring, row => Assert.True(row.FindIndex(c => c.Set == UserRingSet) >= Gutter, "ring starts left of the gutter"));
        Assert.All(ring, row => Assert.True(row.Count(c => c.Set == UserRingSet && c.Slot is 0 or 1 or 2) <= 40 * 3 / 4));
        // Right-aligned, with " You" beside the ring on a middle row.
        var middle = Assert.Single(ring, r => Text(r).Contains(" You"));
        Assert.EndsWith("▒ You ", Text(middle));
        var label = Text(middle).IndexOf(" You", StringComparison.Ordinal);
        Assert.Equal((UserRingSet, RightSlot), (middle[label - 1].Set, middle[label - 1].Slot));
    }

    [Fact]
    public void A_wrapped_user_message_is_at_most_three_quarters_wide_and_right_aligned()
    {
        var text = string.Join(' ', Enumerable.Repeat("words", 40));
        var rows = new Screen().Show(You(text)).Render();
        var ring = rows.Where(r => r.Any(c => c.Set == UserRingSet)).ToList();

        var left = ring.Min(row => row.FindIndex(c => c.Set == UserRingSet));
        var right = ring.Max(row => row.FindLastIndex(c => c.Set == UserRingSet));
        Assert.True(right - left + 1 <= Width * 3 / 4, $"ring is {right - left + 1} wide");
        Assert.True(left >= Gutter, $"ring starts at column {left}");
        Assert.All(ring.Skip(1).SkipLast(1), row => Assert.All(row[(left + 2)..(right - 1)].Where(c => c.Text != " "),
            c => Assert.Equal(Token("Light", "UserPillText"), c.Foreground)));
        var labelled = Assert.Single(ring, r => Text(r).Contains(" You"));
        Assert.Equal(" You", Text(labelled[(right + 1)..(right + 5)]));
    }

    [Fact]
    public void A_tool_line_is_a_one_row_chip_on_the_gutter()
    {
        var rows = new Screen().Show(Hands("Read notes.txt", "succeeded")).Render();
        var chip = rows.Where(r => r.Any(c => c.Set == ToolSet)).ToList();

        var row = Assert.Single(chip);
        Assert.Equal([(ToolSet, LeftSlot), (ToolSet, LeftSlot)], Tiles(row[Gutter..(Gutter + 2)]));
        var label = "Read notes.txt → succeeded";
        Assert.Equal(label, Text(row[(Gutter + 2)..(Gutter + 2 + label.Length)]));
        Assert.Equal([(ToolSet, RightSlot), (ToolSet, RightSlot)], Tiles(row[(Gutter + 2 + label.Length)..(Gutter + 4 + label.Length)]));
        Assert.Equal(Token("Light", "TextMuted"), row[Gutter + 2].Foreground);
        Assert.False(row[Gutter + 2].Italic);
    }

    [Fact]
    public void A_long_tool_line_stays_one_row_and_ends_in_an_ellipsis()
    {
        var rows = new Screen().Show(Hands("Read " + new string('x', 120), "succeeded")).Render();

        var row = Assert.Single(rows, r => r.Any(c => c.Set == ToolSet));
        Assert.Contains("…", Text(row));
        Assert.Equal((ToolSet, RightSlot), (row[^3].Set, row[^3].Slot));
    }

    [Fact]
    public void The_header_shows_approved_between_its_caps()
    {
        var row = new Screen().Render()[0];
        var start = Text(row).IndexOf("APPROVED", StringComparison.Ordinal);

        Assert.Equal([(ApprovedSet, LeftSlot), (ApprovedSet, LeftSlot)], Tiles(row[(start - 2)..start]));
        Assert.Equal([(ApprovedSet, RightSlot), (ApprovedSet, RightSlot)], Tiles(row[(start + 8)..(start + 10)]));
    }

    [Fact]
    public void The_composer_is_framed_on_the_gutter_then_a_blank_row_then_the_keys()
    {
        var rows = new Screen().Render();
        var composer = Indexes(rows, ComposerSet);

        Assert.Equal(3, composer.Count);
        Assert.Equal(composer[0] + 2, composer[^1]);
        Assert.Equal(Gutter, rows[composer[0]].FindIndex(c => c.Set == ComposerSet));
        Assert.Equal(Width - Gutter - 1, rows[composer[0]].FindLastIndex(c => c.Set == ComposerSet));
        // The prompt row: text cells on the card surface between the ring columns.
        Assert.All(rows[composer[1]][(Gutter + 2)..(Width - Gutter - 2)], c => Assert.Equal(Token("Light", "CardSurface"), c.Background));
        // No rule above it, one blank row below it, then the keys on the gutter with no fill of their own.
        Assert.DoesNotContain("─", Text(rows[composer[0] - 1]));
        Assert.Equal("", Text(rows[composer[^1] + 1]).Trim());
        var keys = rows[composer[^1] + 2];
        Assert.StartsWith(new string(' ', Gutter) + "enter send", Text(keys));
        Assert.DoesNotContain(keys, c => c.Background == Token("Light", "SurfaceAlt"));
    }

    [Theory]
    [InlineData("", 3)]
    [InlineData("1\n2\n3", 5)]
    [InlineData("1\n2\n3\n4\n5\n6", 8)]
    [InlineData("1\n2\n3\n4\n5\n6\n7\n8", 8)]
    public void The_composer_grows_up_to_six_lines_inside_its_ring(string text, int rows)
    {
        // Off screen the editor sizes itself from the text it has when first laid out, so each
        // height is a fresh screen; growing and shrinking while typing is checked live.
        var screen = new Screen();
        screen.Composer.Text = text;

        Assert.Equal(rows, Indexes(screen.Render(), ComposerSet).Count);
    }

    [Fact]
    public void A_wrapping_composer_line_is_exactly_as_tall_as_its_wrapped_rows()
    {
        // 80 columns: gutters 4 + 4, ring 2 + 2, prompt 1 leave 67 text columns; this wraps to 3 rows there.
        var text = string.Join(' ', Enumerable.Repeat("wrapping", 20)).TrimEnd();
        var screen = new Screen();
        screen.Composer.Text = text;

        var rows = screen.Render();
        var composer = Indexes(rows, ComposerSet);
        Assert.Equal(3 + 2, composer.Count);
        Assert.Contains("wrapping", Text(rows[composer[^2]]));
    }

    [Fact]
    public void A_code_block_is_framed_also_inside_a_list_and_a_quote()
    {
        var markdown = "Plain:\n\n```\nplain code\n```\n\n- item\n\n  ```\n  listed code\n  ```\n\n> ```\n> quoted code\n> ```\n";
        var rows = new Screen().Show(Card(markdown)).Render();

        foreach (var code in new[] { "plain code", "listed code", "quoted code" })
        {
            var at = rows.FindIndex(r => Text(r).Contains(code));
            Assert.True(at > 0, $"{code} not shown");
            var line = rows[at];
            var start = Text(line).IndexOf(code, StringComparison.Ordinal);
            Assert.Equal([(CodeBlockSet, LeftSlot), (CodeBlockSet, LeftSlot)], Tiles(line[(start - 2)..start]));
            Assert.Contains(rows[at - 1], c => c.Set == CodeBlockSet && c.Slot is >= 0 and <= 2);
            Assert.Contains(rows[at + 1], c => c.Set == CodeBlockSet && c.Slot is >= 5 and <= 7);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    public void Narrow_windows_render_every_shape_without_throwing(int width)
    {
        var screen = new Screen().Show(You("hello"), Card("text\n\n```\ncode\n```\n"), Hands("Read notes.txt", null));

        Assert.NotEmpty(screen.Render(width));
    }

    // ── Screen ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The real ChatScreen (theme Light) with its tile sets at 19×42.</summary>
    private sealed class Screen
    {
        private readonly Type _screenType = Type("ForgeMission.Cli.Tui.ChatScreen");
        private readonly object _screen;

        public Screen()
        {
            var stylesType = Type("ForgeMission.Cli.Tui.ForgeStyles");
            var styles = Activator.CreateInstance(stylesType, Theme("Light"))!;
            var header = Activator.CreateInstance(Type("ForgeMission.Cli.Tui.ChatHeader"), "chat", "Chat", 1, "anthropic")!;
            _screen = Activator.CreateInstance(_screenType, [header, styles])!;
            var cell = Activator.CreateInstance(Type("ForgeMission.Cli.Tui.Graphics.CellSize"), 19, 42)!;
            var tiles = Type("ForgeMission.Cli.Tui.ScreenTiles").GetMethod("Create")!.Invoke(null, [styles, cell]);
            _screenType.GetMethod("UseTiles")!.Invoke(_screen, [tiles]);
        }

        public PromptEditor Composer => (PromptEditor)_screenType.GetProperty("Composer")!.GetValue(_screen)!;

        public Screen Show(params object[] blocks)
        {
            var array = Array.CreateInstance(Type("ForgeMission.Cli.Tui.TranscriptBlock"), blocks.Length);
            for (var i = 0; i < blocks.Length; i++) array.SetValue(blocks[i], i);
            _screenType.GetMethod("Show")!.Invoke(_screen, [array]);
            return this;
        }

        public List<List<Cell>> Render(int width = Width)
        {
            var root = (Visual)_screenType.GetProperty("Root")!.GetValue(_screen)!;
            return [.. VisualSnapshotRenderer.Render(root, width, 40).ToMarkupLines().Select(Cells)];
        }
    }

    private static object Card(string text) => Activator.CreateInstance(Type("ForgeMission.Cli.Tui.ParticipantCard"), "Chat:Answerer", text, "Chat")!;

    private static object You(string text) => Activator.CreateInstance(Type("ForgeMission.Cli.Tui.YouBlock"), text)!;

    private static object Hands(string label, string? outcome) => Activator.CreateInstance(Type("ForgeMission.Cli.Tui.HandsLine"), label, outcome)!;

    // ── Cells from the snapshot's markup ────────────────────────────────────────────────────

    /// <summary>One screen cell: its text element, colours, italic, and, for a placeholder cell,
    /// the tile set and slot its image id names.</summary>
    private sealed record Cell(string Text, Color? Foreground, Color? Background, bool Italic)
    {
        private const string Placeholder = "􎻮";

        private uint? Id => Text.StartsWith(Placeholder, StringComparison.Ordinal) && Foreground is { } c
            ? (uint)(c.R << 16 | c.G << 8 | c.B) : null;

        public int? Set => Id is { } id ? (int)(id >> 3 & 7) : null;

        public int Slot => Id is { } id ? (int)(id & 7) : -1;
    }

    private static List<Cell> Cells(string markup)
    {
        var cells = new List<Cell>();
        (Color? Fg, Color? Bg, bool Italic) style = (null, null, false);
        foreach (Match token in MarkupToken().Matches(markup))
        {
            if (token.Value == "[/]") style = (null, null, false);
            else if (token.Groups["style"].Success) style = ParseStyle(token.Groups["style"].Value);
            else AddText(cells, token.Value is "[[" or "]]" ? token.Value[..1] : token.Value, style);
        }
        return cells;
    }

    private static void AddText(List<Cell> cells, string text, (Color? Fg, Color? Bg, bool Italic) style)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
            cells.Add(new Cell((string)elements.Current, style.Fg, style.Bg, style.Italic));
    }

    private static (Color?, Color?, bool) ParseStyle(string style)
    {
        var parts = style.Split(' ');
        var on = Array.IndexOf(parts, "on");
        Color? fg = parts.Take(on < 0 ? parts.Length : on).Where(p => p.StartsWith('#')).Select(Hex).Cast<Color?>().FirstOrDefault();
        Color? bg = on < 0 ? null : Hex(parts[on + 1]);
        return (fg, bg, parts.Contains("italic"));
    }

    private static Color Hex(string hex)
    {
        var v = Convert.ToUInt32(hex[1..], 16);
        return Color.Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    [GeneratedRegex(@"\[\[|\]\]|\[/\]|\[(?<style>[^\[\]]+)\]|[^\[\]]+")]
    private static partial Regex MarkupToken();

    private static string Text(IEnumerable<Cell> row) => string.Concat(row.Select(c => c.Set is null ? c.Text : "▒"));

    private static (int?, int)[] Tiles(IEnumerable<Cell> cells) => [.. cells.Select(c => (c.Set, c.Slot))];

    private static List<int> Indexes(List<List<Cell>> rows, int set) =>
        [.. rows.Select((row, i) => (row, i)).Where(r => r.row.Any(c => c.Set == set)).Select(r => r.i)];

    // ── Reflection ──────────────────────────────────────────────────────────────────────────

    private static Color Token(string theme, string name) => (Color)Type("ForgeMission.Cli.Tui.ForgeTheme").GetProperty(name)!.GetValue(Theme(theme))!;

    private static object Theme(string name) =>
        Type("ForgeMission.Cli.Tui.ForgeTheme").GetProperty(name, BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;

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
