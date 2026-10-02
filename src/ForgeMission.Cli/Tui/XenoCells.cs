using System.Runtime.CompilerServices;
using System.Text;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Rendering;

namespace ForgeMission.Cli.Tui;

// Phase 56 Task 5 — Type-2 exception (supervisor, 2026-10-02): the only code that touches
// XenoAtom.Terminal.UI internals. XenoAtom 3.10.0's public API cannot read a rendered cell back,
// cannot say which link is under the pointer, and cannot restart an animation that has gone idle;
// its translucent overlay also discards a cell's own text colour. The motion overlays (FadeIn,
// StreamCaret, LinkPointer) need all three, so they read cells here. Six internal members, through
// [UnsafeAccessor] (resolved at compile time, AOT-safe): CellBuffer.UnsafeScalars, UnsafeCells,
// UnsafeHyperlinks, CurrentClipRect, TryGetTextElement, and TerminalApp.RequestAnimation.
// XenoInternalsContractTests pins every one of them to XenoAtom 3.10.0, and TuiColourLiteralTests
// fails if [UnsafeAccessor] appears anywhere else.
// Removal condition: XenoAtom exposes public cell reads and hit-testing (request raised upstream);
// then this file goes and the overlays use the public API.

/// <summary>What a rendered cell holds: its glyph (a rune, or a multi-code-point text element),
/// its style, whether it carries a link, and whether it is a kitty image placeholder.</summary>
internal readonly record struct CellFacts(int Rune, string? Element, Style Style, bool Link)
{
    private const int PlaceholderRune = 0x10EEEE;

    /// <summary>A kitty placeholder (U+10EEEE plus its diacritics): its colour is an image id.</summary>
    public bool Placeholder => Rune == PlaceholderRune || (Element is { Length: >= 2 } e && char.ConvertToUtf32(e, 0) == PlaceholderRune);

    /// <summary>A space: nothing to fade, not the end of the text.</summary>
    public bool Blank => Element is null && Rune == ' ';

    /// <summary>Whether two cells show the same glyph.</summary>
    public bool SameGlyph(CellFacts other) => Rune == other.Rune && Element == other.Element;
}

internal static class XenoCells
{
    /// <summary>The cell at (x, y), which must lie inside the buffer.</summary>
    public static CellFacts Read(CellBuffer buffer, int x, int y)
    {
        var i = y * buffer.Width + x;
        var scalar = Scalars(buffer)[i];
        string? element = null;
        if (scalar < 0 && TryGetTextElement(buffer, scalar, out var text, out _)) element = text;
        return new CellFacts(scalar < 0 ? -1 : scalar, element, Cells(buffer)[i], Hyperlinks(buffer)[i] != 0);
    }

    /// <summary>The cells a visual rendering now may read and restyle: the current clip, inside
    /// the buffer.</summary>
    public static Rectangle Clip(CellBuffer buffer)
    {
        var clip = CurrentClipRect(buffer);
        var x0 = Math.Max(0, clip.X);
        var y0 = Math.Max(0, clip.Y);
        var x1 = Math.Min(buffer.Width, clip.X + clip.Width);
        var y1 = Math.Min(buffer.Height, clip.Y + clip.Height);
        return new Rectangle(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>Makes the app ask every animated visual for its next tick again: an animation
    /// that was idle (long.MaxValue) has a new deadline.</summary>
    public static void RequestAnimation(TerminalApp app) => RequestAnimationCore(app);

    /// <summary>A cell's glyph as text, for tests and diagnostics.</summary>
    public static string Glyph(CellFacts cell) => cell.Element ?? (cell.Rune >= 0 ? new Rune(cell.Rune).ToString() : "");

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_UnsafeScalars")]
    private static extern ReadOnlySpan<int> Scalars(CellBuffer buffer);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_UnsafeCells")]
    private static extern ReadOnlySpan<Style> Cells(CellBuffer buffer);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_UnsafeHyperlinks")]
    private static extern ReadOnlySpan<ulong> Hyperlinks(CellBuffer buffer);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentClipRect")]
    private static extern Rectangle CurrentClipRect(CellBuffer buffer);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TryGetTextElement")]
    private static extern bool TryGetTextElement(CellBuffer buffer, int token, out string text, out int width);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "RequestAnimation")]
    private static extern void RequestAnimationCore(TerminalApp app);
}
