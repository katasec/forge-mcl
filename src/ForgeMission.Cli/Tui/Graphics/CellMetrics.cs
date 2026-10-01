using XenoAtom.Terminal;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 G8: what the terminal says about images. The environment facts (graphics protocols,
// multiplexer, colour level) come from XenoAtom's environment detection and send nothing to the
// terminal, so they are read before sign-in. The cell size needs XenoAtom's probe (CSI 14t/16t/18t,
// the input loop consumes the replies, null after the probe timeout); it runs only inside the TUI,
// where XenoAtom already owns terminal input, so nothing before Run touches the terminal's modes.

/// <summary>The terminal environment as XenoAtom detected it.</summary>
internal sealed record TerminalEnvironment(IReadOnlyList<TerminalGraphicsProtocol> Protocols, bool IsMultiplexer,
    TerminalColorLevel Colors);

internal static class CellMetrics
{
    public static TerminalEnvironment Environment() => new(
        Terminal.Graphics.Capabilities.SupportedProtocols,
        Terminal.Graphics.Capabilities.IsMultiplexer,
        Terminal.Capabilities.ColorLevel);

    /// <summary>Asks the terminal for its cell size. Call only while the TUI runs.</summary>
    public static ValueTask<TerminalPixelMetrics?> QueryAsync() => Terminal.Graphics.QueryPixelMetricsAsync();
}
