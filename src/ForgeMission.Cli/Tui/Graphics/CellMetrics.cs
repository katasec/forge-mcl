using XenoAtom.Terminal;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 G8: what the terminal says about images, read once at start-up before the TUI. The
// graphics protocol and colour level come from XenoAtom's environment detection (no probe); the
// cell size needs XenoAtom's probe (CSI 14t/16t/18t, the input loop consumes the replies, null
// after the probe timeout), which needs terminal input running.

/// <summary>The terminal's image facts; <see cref="Metrics"/> is null when not probed or unanswered.</summary>
internal sealed record TerminalFacts(IReadOnlyList<TerminalGraphicsProtocol> Protocols, TerminalColorLevel Colors,
    TerminalPixelMetrics? Metrics);

internal static class CellMetrics
{
    /// <summary>The protocols and colour level XenoAtom detected from the environment.</summary>
    public static (IReadOnlyList<TerminalGraphicsProtocol> Protocols, TerminalColorLevel Colors) Environment() =>
        (Terminal.Graphics.Capabilities.SupportedProtocols, Terminal.Capabilities.ColorLevel);

    /// <summary>Asks the terminal for its cell size. Input runs only for the probe: it starts with
    /// Ctrl-C as input (the options TerminalApp itself uses), so a Ctrl-C cannot end the process
    /// mid-probe in cbreak mode, and stops afterwards, which restores the terminal for the line
    /// output, sign-in and the --hands prompt that follow. Terminal.RunAsync starts input again.</summary>
    public static async Task<TerminalPixelMetrics?> QueryAsync()
    {
        Terminal.StartInput(new TerminalInputOptions { TreatControlCAsInput = true });
        try
        {
            return await Terminal.Graphics.QueryPixelMetricsAsync();
        }
        finally
        {
            await Terminal.StopInputAsync();
        }
    }
}
