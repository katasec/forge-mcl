using System.Text;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (Phase 56): the only code that writes raw escape sequences to stdout — kitty
// images (KittyImages) and the caret colour (TerminalCaret). It writes straight to the stdout
// stream because XenoAtom's terminal writer does not reach the terminal while the app runs
// (docs/design/tui-graphics.md). TuiColourLiteralTests allows raw stdout here only.
internal static class RawStdout
{
    public static void Write(string escape)
    {
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(Encoding.UTF8.GetBytes(escape));
        stdout.Flush();
    }
}
