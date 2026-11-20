using ForgeMission.Core.Resolution;
using ForgeMission.Core.Runtime;
using ForgeMission.Parser;

namespace ForgeMission.Core.Experts;

/// <summary>
/// Validates a parsed mission against its loaded experts: every step resolves, mission references
/// have no cycle, and each step's inputKeys are declared upstream with the same type.
/// </summary>
public static class MissionValidator
{
    public static void Validate(
        Program ast,
        Dictionary<string, ExpertDefinition> experts,
        TextWriter? warnings = null,
        bool contractErrorsAreFatal = false,
        string? missionFilePath = null,
        IReadOnlyDictionary<string, string>? expertMarkdownByName = null)
    {
        var missionsByName = ast.Declarations
            .OfType<MissionDeclaration>()
            .ToDictionary(m => m.Name, StringComparer.Ordinal);

        var missionNames = missionsByName.Keys.ToHashSet(StringComparer.Ordinal);

        var missionParams = ast.Declarations
            .OfType<MissionDeclaration>()
            .SelectMany(m => m.Params)
            .ToHashSet(StringComparer.Ordinal);

        var allSteps = ast.Declarations
            .OfType<MissionDeclaration>()
            .SelectMany(m => m.Pipeline.Steps().Select(s => s.ExpertName))
            .Distinct(StringComparer.Ordinal);

        var missing = allSteps
            .Where(step => !experts.ContainsKey(step)
                        && !missionNames.Contains(step)
                        && !missionParams.Contains(step))
            .OrderBy(s => s)
            .ToList();

        if (missing.Count > 0)
            throw new ExpertLoadException(
                $"Missing expert definitions for: {string.Join(", ", missing)}. " +
                "Each expert must have a matching markdown file in the experts directory.");

        ValidateMissionReferenceCycles(missionsByName, missionFilePath);

        var contractIssues = ast.Declarations
            .OfType<MissionDeclaration>()
            .SelectMany(m => ValidateContextKeys(m, experts, expertMarkdownByName))
            .ToList();

        if (contractIssues.Count == 0) return;

        if (contractErrorsAreFatal)
            throw new AggregateExpertLoadException(contractIssues);

        if (warnings is not null)
            foreach (var issue in contractIssues)
                warnings.WriteLine($"warning {issue.Message}");
    }

    private static void ValidateMissionReferenceCycles(
        Dictionary<string, MissionDeclaration> missionsByName,
        string? missionFilePath)
    {
        var graph = BuildMissionReferenceGraph(missionsByName);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var inStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new List<string>();

        foreach (var missionName in missionsByName.Keys)
            if (!visited.Contains(missionName))
                Visit(missionName);

        void Visit(string missionName)
        {
            visited.Add(missionName);
            inStack.Add(missionName);
            stack.Add(missionName);

            foreach (var edge in graph.GetValueOrDefault(missionName, []))
            {
                if (!visited.Contains(edge.ToMission))
                {
                    Visit(edge.ToMission);
                    continue;
                }

                if (!inStack.Contains(edge.ToMission)) continue;

                var cycle = CyclePath(stack, edge.ToMission);
                var message = $"Circular mission reference: {FormatCycle(cycle)}";
                var span = edge.Step.Span;
                throw new ExpertLoadException(
                    message,
                    missionFilePath,
                    span?.StartLine ?? 0,
                    span?.StartCol ?? 0,
                    span?.EndCol ?? -1);
            }

            stack.RemoveAt(stack.Count - 1);
            inStack.Remove(missionName);
        }
    }

    private static Dictionary<string, List<MissionReferenceEdge>> BuildMissionReferenceGraph(
        Dictionary<string, MissionDeclaration> missionsByName)
    {
        var graph = new Dictionary<string, List<MissionReferenceEdge>>(StringComparer.Ordinal);
        foreach (var mission in missionsByName.Values)
        {
            foreach (var step in mission.Pipeline.Steps())
            {
                if (!missionsByName.ContainsKey(step.ExpertName)) continue;

                if (!graph.TryGetValue(mission.Name, out var edges))
                {
                    edges = [];
                    graph[mission.Name] = edges;
                }
                edges.Add(new MissionReferenceEdge(mission.Name, step.ExpertName, step));
            }
        }
        return graph;
    }

    private static List<string> CyclePath(List<string> stack, string repeatedMission)
    {
        var start = stack.FindIndex(m => string.Equals(m, repeatedMission, StringComparison.Ordinal));
        var cycle = stack[start..].ToList();
        cycle.Add(repeatedMission);
        return cycle;
    }

    private static string FormatCycle(List<string> cycle) =>
        string.Join(" -> ", cycle.Select(m => $"'{m}'"));

    // Walk the pipeline accumulating declared outputKeys; emit diagnostics when a step's
    // inputKeys reference a key that no upstream step has declared, or with a mismatched type.
    // Diagnostics point at the offending key line inside the expert's expert.md file.
    private static IEnumerable<ExpertLoadException> ValidateContextKeys(
        MissionDeclaration mission,
        Dictionary<string, ExpertDefinition> experts,
        IReadOnlyDictionary<string, string>? expertMarkdownByName)
    {
        var available = RuntimeKeys(mission);
        foreach (var step in mission.Pipeline.Steps())
        {
            if (!experts.TryGetValue(step.ExpertName, out var expert)) continue;

            foreach (var issue in CheckInputKeys(mission, step, expert, available, expertMarkdownByName))
                yield return issue;

            AddOutputKeys(expert, available);
        }
    }

    // The standard runtime keys every pipeline starts with, plus the mission's parameters.
    private static Dictionary<string, string> RuntimeKeys(MissionDeclaration mission)
    {
        var available = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ContextKeys.Output]    = "string",
            [ContextKeys.Feedback]  = "string",
            [ContextKeys.MaxLoops] = "int",
        };
        foreach (var parameter in mission.Params)
            available.TryAdd(parameter, "string");
        return available;
    }

    // One diagnostic per inputKey that no upstream step declares, or declares with another type.
    private static IEnumerable<ExpertLoadException> CheckInputKeys(
        MissionDeclaration mission,
        Step step,
        ExpertDefinition expert,
        Dictionary<string, string> available,
        IReadOnlyDictionary<string, string>? expertMarkdownByName)
    {
        if (expert.InputKeys is not { } inputKeys) yield break;

        var expertFile = Path.Combine(expert.ExpertDirectory, SourceResolver.ExpertFile);
        var frontmatter = DiagnosticFrontmatter(expertFile, expert.Name, expertMarkdownByName);

        foreach (var (key, declaredType) in inputKeys)
        {
            if (InputKeyProblem(mission.Name, step.ExpertName, key, declaredType, available) is not { } message)
                continue;

            var (line, col, endCol) = FindKeyInBlock(frontmatter, "inputKeys", key, expertFile);
            yield return new ExpertLoadException(message, expertFile, line, col, endCol);
        }
    }

    // MCL011 when no upstream step declares the key; MCL012 when its type differs; else null.
    private static string? InputKeyProblem(
        string missionName, string expertName, string key, string declaredType, Dictionary<string, string> available)
    {
        if (!available.TryGetValue(key, out var upstreamType))
            return $"{MclErrorCode.InputKeyMissingUpstream.Code()}: expert '{expertName}' in mission '{missionName}' " +
                   $"declares inputKey '{key}: {declaredType}' but no upstream expert declares this outputKey.";

        if (string.Equals(upstreamType, declaredType, StringComparison.OrdinalIgnoreCase)) return null;

        return $"{MclErrorCode.InputKeyTypeMismatch.Code()}: expert '{expertName}' in mission '{missionName}' " +
               $"expects '{key}: {declaredType}' but upstream declares '{key}: {upstreamType}'.";
    }

    // Makes this step's outputKeys available to downstream steps.
    private static void AddOutputKeys(ExpertDefinition expert, Dictionary<string, string> available)
    {
        if (expert.OutputKeys is not { } outputKeys) return;
        foreach (var (key, type) in outputKeys)
            available[key] = type;
    }

    private static (string text, int startLine) DiagnosticFrontmatter(
        string path, string name, IReadOnlyDictionary<string, string>? markdownByName)
    {
        if (markdownByName is null) return ExpertLoader.ReadFrontmatter(path);
        if (!markdownByName.TryGetValue(name, out var markdown))
            throw new ExpertLoadException($"Missing immutable diagnostic source for expert '{name}'.");
        var (text, _, startLine) = ExpertLoader.SplitFrontmatter(path, markdown);
        return (text, startLine);
    }

    // Find a specific key under a YAML block heading (e.g. key under "inputKeys:").
    // Returns (absolute line, col, endCol) suitable for ExpertLoadException.
    private static (int line, int col, int endCol) FindKeyInBlock(
        (string text, int startLine) frontmatter, string blockName, string keyName, string filePath)
    {
        var lines = frontmatter.text.Split('\n');
        bool inBlock = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(blockName + ":", StringComparison.Ordinal)) { inBlock = true; continue; }
            if (inBlock)
            {
                // Leave the block when we hit a non-indented line that isn't this key.
                if (lines[i].Length > 0 && lines[i][0] != ' ' && lines[i][0] != '\t') inBlock = false;
                else if (trimmed.StartsWith(keyName + ":", StringComparison.Ordinal))
                {
                    var col = lines[i].Length - trimmed.Length;
                    return (frontmatter.startLine + i, col, col + keyName.Length);
                }
            }
        }
        // Fall back to pointing at the inputKeys: heading itself.
        return ExpertLoader.FindField(frontmatter.text, blockName, frontmatter.startLine);
    }

    private sealed record MissionReferenceEdge(string FromMission, string ToMission, Step Step);
}
