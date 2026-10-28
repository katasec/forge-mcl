using ForgeMission.Core.Experts;
using ForgeMission.Parser;
using MclProgram = ForgeMission.Parser.Program;

namespace ForgeMission.Cli.Services;

// Shared CLI helpers used across several commands: file reading, mission parsing, expert loading
// and validation, the mcl.lock path, the expert scaffold template, and context lookup. Each
// reports its own failure through CliDiagnostics (Die / the diagnostic renderers), so callers read
// top-down.
internal static class CliHelpers
{
    internal static async Task<string?> TryReadFile(string path, CancellationToken cancellationToken)
    {
        try { return await File.ReadAllTextAsync(path, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { CliDiagnostics.Die($"Cannot read file '{path}': {ex.Message}"); return null; }
    }

    internal static MclProgram? TryParse(string source, string filePath)
    {
        var result = MclParser.TryParse(source);
        if (!result.Success)
        {
            foreach (var d in result.Diagnostics)
                CliDiagnostics.ReportDiagnostic(source, filePath, d);
            throw new ForgeExitException(1);
        }
        return result.Ast!;
    }

    internal static Dictionary<string, ExpertDefinition>? TryLoadExperts(string expertsDir)
    {
        if (!Directory.Exists(expertsDir)) { CliDiagnostics.Die($"Experts directory not found: {expertsDir}"); return null; }
        try { return new ExpertLoader(expertsDir).LoadAll(); }
        catch (AggregateExpertLoadException ex) { foreach (var e in ex.Errors) CliDiagnostics.ReportExpertDiagnostic(e); throw new ForgeExitException(1); }
        catch (ExpertLoadException ex) { CliDiagnostics.ReportExpertDiagnostic(ex); throw new ForgeExitException(1); }
    }

    internal static bool TryValidate(
        MclProgram ast,
        Dictionary<string, ExpertDefinition> experts,
        bool contractErrorsAreFatal = false,
        string? missionFilePath = null)
    {
        try
        {
            ExpertLoader.Validate(ast, experts, Console.Error, contractErrorsAreFatal, missionFilePath);
            return true;
        }
        catch (AggregateExpertLoadException ex) { foreach (var e in ex.Errors) CliDiagnostics.ReportExpertDiagnostic(e); return false; }
        catch (ExpertLoadException ex)          { CliDiagnostics.ReportExpertDiagnostic(ex); return false; }
    }

    // The lock file lives beside the mission; one place builds its path.
    internal static string MclLockPath(string missionDir) => Path.Combine(missionDir, "mcl.lock");

    internal static string ExpertTemplate(string name) => $"""
        ---
        name: {name}
        version: 0.1.0
        description: [One-line description of what this expert does]
        input: [Input description]
        output: [Output description]
        ---

        You are a [role description].

        Your job is to:
        1. [Step one]
        2. [Step two]
        3. [Step three]

        Produce [output description].
        """;

    internal static string? GetContextString(Dictionary<string, object> ctx, string key)
        => ctx.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;
}
