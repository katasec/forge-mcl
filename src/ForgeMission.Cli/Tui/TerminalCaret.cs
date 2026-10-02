using XenoAtom.Terminal.UI;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (Phase 56 Task 3 review): the composer caret is the terminal's own cursor
// (XenoAtom only places it and shows it), so its colour is the user's terminal theme — a light
// cursor is hard to see on the light theme's white composer. While the TUI runs, the cursor takes
// the theme's Caret token (OSC 12); on every way out — quit, the G8 stop, an exception — the
// user's own cursor colour comes back (OSC 112), so the shell is never left recoloured.
internal static class TerminalCaret
{
    /// <summary>OSC 112: back to the terminal's configured cursor colour.</summary>
    public const string RestoreEscape = "\u001b]112\u001b\\";

    /// <summary>Runs <paramref name="run"/> with the cursor in <paramref name="caret"/>.</summary>
    public static Task WhileRunning(Color caret, Func<Task> run) => WhileRunning(caret, run, RawStdout.Write);

    /// <summary>OSC 12: the cursor colour, as rgb:rr/gg/bb.</summary>
    public static string SetEscape(Color caret) => $"\u001b]12;rgb:{caret.R:x2}/{caret.G:x2}/{caret.B:x2}\u001b\\";

    /// <summary><see cref="WhileRunning(Color, Func{Task})"/> with the escape writer given, so tests
    /// can read what is written on each exit path.</summary>
    internal static async Task WhileRunning(Color caret, Func<Task> run, Action<string> write)
    {
        write(SetEscape(caret));
        try
        {
            await run();
        }
        finally
        {
            write(RestoreEscape);
        }
    }
}
