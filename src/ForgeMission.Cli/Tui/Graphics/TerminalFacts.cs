using XenoAtom.Terminal;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 G8: what the terminal says about images, and the two decisions forge chat makes from it.
// The environment facts (graphics protocols, multiplexer, colour level) come from XenoAtom's
// environment detection and send nothing to the terminal, so they are read before sign-in. The
// cell size needs XenoAtom's probe (CSI 14t/16t/18t; the input loop consumes the replies; null
// after the probe timeout); it runs only inside the TUI, where XenoAtom already owns input, so
// nothing before Run touches the terminal's modes.

/// <summary>A terminal cell in device px.</summary>
internal readonly record struct CellSize(int Width, int Height);

/// <summary>The terminal environment as XenoAtom detected it.</summary>
internal sealed record TerminalEnvironment(IReadOnlyList<TerminalGraphicsProtocol> Protocols, bool IsMultiplexer,
    TerminalColorLevel Colors);

internal static class TerminalFacts
{
    public static TerminalEnvironment Environment() => new(
        Terminal.Graphics.Capabilities.SupportedProtocols,
        Terminal.Graphics.Capabilities.IsMultiplexer,
        Terminal.Capabilities.ColorLevel);

    /// <summary>Asks the terminal for its cell size. Call only while the TUI runs.</summary>
    public static ValueTask<TerminalPixelMetrics?> QueryCellAsync() => Terminal.Graphics.QueryPixelMetricsAsync();

    /// <summary>G8, before sign-in: kitty graphics, not inside a multiplexer (tmux answers the
    /// cell-size query but drops kitty images), and truecolor (the image id is a placeholder's
    /// 24-bit colour).</summary>
    public static bool ShowsImages(TerminalEnvironment environment) =>
        environment.Protocols.Contains(TerminalGraphicsProtocol.Kitty) && !environment.IsMultiplexer &&
        environment.Colors == TerminalColorLevel.TrueColor;

    /// <summary>G8, on the TUI's first tick: the cell size the terminal answered, or null when it
    /// did not answer (or answered 0×0) and the TUI cannot draw its images.</summary>
    public static CellSize? ImageCell(TerminalPixelMetrics? metrics) =>
        metrics is { CellPixelWidth: > 0, CellPixelHeight: > 0 } m ? new CellSize(m.CellPixelWidth, m.CellPixelHeight) : null;
}
