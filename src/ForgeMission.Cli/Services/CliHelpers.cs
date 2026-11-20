using System.Diagnostics;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Manifest;
using ForgeMission.Parser;
using MclProgram = ForgeMission.Parser.Program;

namespace ForgeMission.Cli.Services;

// Shared CLI helpers used across several commands: file reading, mission parsing, expert loading
// and validation, the mcl.lock path, the expert scaffold template, and context lookup. Each
// reports its own failure through CliDiagnostics (Die / the diagnostic renderers), so callers read
// top-down.
internal static class CliHelpers
{
    internal static async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try { return await File.ReadAllTextAsync(path, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { CliDiagnostics.Die($"Cannot read file '{path}': {ex.Message}"); throw new UnreachableException(); }
    }

    // The mission-load opening shared by `forge init`, `forge validate` and `forge mcp`: read the
    // mission file, parse it, then read forge.toml beside it. Each step reports its own failure.
    internal static async Task<(MclProgram Ast, ForgeManifest? Manifest)> LoadMissionAsync(string missionPath, CancellationToken cancellationToken)
    {
        var source = await ReadFileAsync(missionPath, cancellationToken);
        var ast    = Parse(source, missionPath);

        ForgeManifest? manifest = null;
        try { manifest = ForgeTomlReader.TryRead(missionPath); }
        catch (ForgeTomlException ex) { CliDiagnostics.Die(ex.Message); }

        return (ast, manifest);
    }

    internal static MclProgram Parse(string source, string filePath)
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

    internal static Dictionary<string, ExpertDefinition> LoadExperts(string expertsDir)
    {
        if (!Directory.Exists(expertsDir)) CliDiagnostics.Die($"Experts directory not found: {expertsDir}");
        try { return new ExpertLoader(expertsDir).LoadAll(); }
        catch (Exception ex) when (ex is ExpertLoadException or AggregateExpertLoadException)
        { CliDiagnostics.ReportExpertLoadFailure(ex); throw new ForgeExitException(1); }
    }

    internal static bool TryValidate(
        MclProgram ast,
        Dictionary<string, ExpertDefinition> experts,
        bool contractErrorsAreFatal = false,
        string? missionFilePath = null)
    {
        try
        {
            MissionValidator.Validate(ast, experts, Console.Error, contractErrorsAreFatal, missionFilePath);
            return true;
        }
        catch (Exception ex) when (ex is ExpertLoadException or AggregateExpertLoadException)
        { CliDiagnostics.ReportExpertLoadFailure(ex); return false; }
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
