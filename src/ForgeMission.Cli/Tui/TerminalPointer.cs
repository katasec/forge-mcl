namespace ForgeMission.Cli.Tui;

// forge chat TUI (Phase 56 Task 5): the mouse pointer's shape. Over a link (LinkPointer) it is a
// hand (OSC 22 with ForgeTheme.LinkPointerShape); elsewhere the terminal's default. Like the caret
// colour (TerminalCaret), the default comes back on every way out — quit, the G8 stop, an exception
// — so the shell is never left with a hand. The reset is written even when the hand was never set:
// it costs one escape and needs no state.
internal static class TerminalPointer
{
    /// <summary>OSC 22: the hand over a link.</summary>
    public const string HandEscape = "\u001b]22;" + ForgeTheme.LinkPointerShape + "\u001b\\";

    /// <summary>OSC 22: back to the terminal's default pointer.</summary>
    public const string DefaultEscape = "\u001b]22;default\u001b\\";

    /// <summary>Runs <paramref name="run"/>, then puts the default pointer back however it ends.</summary>
    public static Task WhileRunning(Func<Task> run) => WhileRunning(run, RawStdout.Write);

    /// <summary><see cref="WhileRunning(Func{Task})"/> with the escape writer given, so tests can
    /// read what is written on each exit path.</summary>
    internal static async Task WhileRunning(Func<Task> run, Action<string> write)
    {
        try
        {
            await run();
        }
        finally
        {
            write(DefaultEscape);
        }
    }
}
