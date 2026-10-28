using ForgeMission.Core.Experts;
using ForgeMission.Parser;

namespace ForgeMission.Cli.Services;

// Shared CLI diagnostic output: the rustc-style error renderer, the two report helpers that feed it
// (parse diagnostics and expert-load diagnostics), the ANSI bold/colour wrappers, and Die. Kept
// together because Die and the report helpers all route through the same ANSI wrappers, and many
// commands depend on them.
internal static class CliDiagnostics
{
    internal static void ReportDiagnostic(string source, string filePath, Diagnostic d)
    {
        var lines      = source.Split('\n');
        var sourceLine = d.Line >= 1 && d.Line <= lines.Length
            ? lines[d.Line - 1].TrimEnd('\r')
            : string.Empty;
        RenderDiagnostic(d.Message, filePath, d.Line, d.Column, d.EndColumn, sourceLine);
    }

    internal static void ReportExpertDiagnostic(ExpertLoadException ex)
    {
        // No file path — fall back to plain Die-style output
        if (ex.FilePath is null || ex.Line == 0) { Die(ex.Message); return; }

        var sourceLine = File.Exists(ex.FilePath)
            ? File.ReadLines(ex.FilePath).ElementAtOrDefault(ex.Line - 1) ?? string.Empty
            : string.Empty;
        RenderDiagnostic(ex.Message, ex.FilePath, ex.Line, ex.Column, ex.EndColumn, sourceLine);
    }

    // One rustc-style diagnostic block on stderr: the message, a `-->` location line, and the source
    // line with carets under [column, endColumn). The two callers differ only in how they obtain the
    // source line (in-memory parse source vs a file on disk).
    internal static void RenderDiagnostic(string message, string filePath, int line, int column, int endColumn, string sourceLine)
    {
        var lineNumStr = line.ToString();
        var gutter     = new string(' ', lineNumStr.Length);
        var col        = Math.Max(0, column);
        var caretLen   = endColumn > column ? endColumn - column : 1;
        var carets     = new string('^', caretLen);
        var spaces     = new string(' ', Math.Min(col, sourceLine.Length));

        var e = Console.Error;
        e.WriteLine($"{BoldRed("error")}{Bold($": {message}")}");
        e.WriteLine($"  {BoldBlue("-->")} {filePath}:{line}:{col + 1}");
        e.WriteLine($"{BoldBlue($"{gutter}   |")}");
        e.WriteLine($"{BoldBlue($"{lineNumStr}   |")} {sourceLine}");
        e.WriteLine($"{BoldBlue($"{gutter}   |")} {spaces}{BoldRed(carets)}");
        e.WriteLine($"{BoldBlue($"{gutter}   |")}");
    }

    // ANSI helpers — bold+color when stderr is a terminal and NO_COLOR is unset
    internal static bool UseAnsi() =>
        !Console.IsErrorRedirected &&
        Environment.GetEnvironmentVariable("NO_COLOR") is null;

    internal static string Bold(string s)       => UseAnsi() ? $"\x1b[1m{s}\x1b[0m"    : s;
    internal static string BoldRed(string s)    => UseAnsi() ? $"\x1b[1;31m{s}\x1b[0m" : s;
    internal static string BoldYellow(string s) => UseAnsi() ? $"\x1b[1;33m{s}\x1b[0m" : s;
    internal static string BoldBlue(string s)   => UseAnsi() ? $"\x1b[1;34m{s}\x1b[0m" : s;

    internal static void Die(string message)
    {
        Console.Error.WriteLine($"{BoldRed("error")}{Bold($": {message}")}");
        throw new ForgeExitException(1);
    }
}
