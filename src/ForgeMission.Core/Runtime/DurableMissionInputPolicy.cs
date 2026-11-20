using ForgeMission.Core.Experts;
using ForgeMission.Parser;

namespace ForgeMission.Core.Runtime;

// One value-only input policy shared by package admission and root-tool replay.
internal static class DurableMissionInputPolicy
{
    internal static IReadOnlyList<string> AdmittedNames(
        Program ast, IReadOnlyDictionary<string, ExpertDefinition> experts, string rootName)
    {
        var root = ast.FindMission(rootName);
        return (root?.Params ?? []).Concat(ReachableSteps(ast, rootName)
                .Where(step => experts.ContainsKey(step.ExpertName))
                .SelectMany(step => experts[step.ExpertName].Inputs ?? []))
            .Where(name => IsIdentifier(name) && !ContextKeys.IsReserved(name))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal static IEnumerable<Step> ReachableSteps(Program ast, string rootName)
    {
        var missions = ast.Missions.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return Visit(rootName);

        IEnumerable<Step> Visit(string name)
        {
            if (!visited.Add(name) || !missions.TryGetValue(name, out var mission)) yield break;
            foreach (var step in mission.Pipeline.Steps())
            {
                if (!missions.ContainsKey(step.ExpertName)) { yield return step; continue; }
                foreach (var child in Visit(step.ExpertName)) yield return child;
            }
        }
    }

    internal static bool IsIdentifier(string name) => !string.IsNullOrEmpty(name)
        && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
