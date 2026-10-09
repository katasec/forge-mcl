using ForgeMission.Core.Experts;
using ForgeMission.Parser;

namespace ForgeMission.Core.Runtime;

// One value-only input policy shared by package admission and root-tool replay.
internal static class DurableMissionInputPolicy
{
    internal static IReadOnlyList<string> AdmittedNames(
        Program ast, IReadOnlyDictionary<string, ExpertDefinition> experts, string rootName)
    {
        var root = ast.Declarations.OfType<MissionDeclaration>().FirstOrDefault(m => m.Name == rootName);
        return (root?.Params ?? []).Concat(ReachableSteps(ast, rootName)
                .Where(step => experts.ContainsKey(step.ExpertName))
                .SelectMany(step => experts[step.ExpertName].Inputs ?? []))
            .Where(name => IsIdentifier(name) && !IsReserved(name))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal static IEnumerable<Step> ReachableSteps(Program ast, string rootName)
    {
        var missions = ast.Declarations.OfType<MissionDeclaration>().ToDictionary(m => m.Name, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return Visit(rootName);

        IEnumerable<Step> Visit(string name)
        {
            if (!visited.Add(name) || !missions.TryGetValue(name, out var mission)) yield break;
            foreach (var step in Steps(mission.Pipeline))
            {
                if (!missions.ContainsKey(step.ExpertName)) { yield return step; continue; }
                foreach (var child in Visit(step.ExpertName)) yield return child;
            }
        }
    }

    internal static IEnumerable<Step> Steps(Pipeline pipeline) => pipeline.Elements.SelectMany(element => element switch
    {
        StepElement step => (IEnumerable<Step>)[step.Step],
        ParallelElement parallel => parallel.Steps,
        _ => [],
    });

    internal static bool IsIdentifier(string name) => !string.IsNullOrEmpty(name)
        && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    internal static bool IsRuntimeDirectory(string name) => name is "work_dir" or "input_dir" or "output_dir";
    internal static bool IsReserved(string name) => name is "output" or "feedback" or "attempt" or "max_loops"
        or "history" or "conversation" or "apiKey" or "model" or "provider" or "endpoint"
        || IsRuntimeDirectory(name) || name.StartsWith("__", StringComparison.Ordinal)
        || name.StartsWith("FORGE_", StringComparison.Ordinal);
}
