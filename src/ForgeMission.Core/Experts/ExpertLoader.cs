using System.Diagnostics.CodeAnalysis;
using ForgeMission.Core.Resolution;
using ForgeMission.Core.Runtime;
using ForgeMission.Parser;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ForgeMission.Core.Experts;

public class ExpertLoader(string expertsDirectory)
{
    private static readonly IDeserializer Yaml = BuildYaml();
    private const string FrontmatterDelimiter = "---";

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ExpertFrontmatter))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(List<string>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Dictionary<string, string>))]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Type preserved via DynamicDependency")]
    private static IDeserializer BuildYaml() => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Load experts from a directory, collecting all errors before surfacing them.</summary>
    public Dictionary<string, ExpertDefinition> LoadAll()
    {
        if (!Directory.Exists(expertsDirectory))
            throw new ExpertLoadException($"Experts directory not found: {expertsDirectory}");

        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors  = new List<ExpertLoadException>();

        void TryLoad(string mdPath)
        {
            try
            {
                var expert = ParseFile(mdPath);
                if (sources.TryGetValue(expert.Name, out var firstPath))
                {
                    errors.Add(DuplicateNameError(expert.Name, firstPath, mdPath));
                    return;
                }
                experts[expert.Name] = expert;
                sources[expert.Name] = mdPath;
            }
            catch (AggregateExpertLoadException ex) { errors.AddRange(ex.Errors); }
            catch (ExpertLoadException ex)           { errors.Add(ex); }
        }

        // Directory-per-expert: experts/Name/expert.md
        foreach (var dir in Directory.GetDirectories(expertsDirectory))
        {
            var expertMd = Path.Combine(dir, SourceResolver.ExpertFile);
            if (File.Exists(expertMd)) TryLoad(expertMd);
        }

        // Flat fallback: experts/Name.md
        foreach (var file in Directory.GetFiles(expertsDirectory, "*.md"))
            TryLoad(file);

        if (errors.Count > 0)
            throw new AggregateExpertLoadException(errors);

        return experts;
    }

    // The expert name an error shows when frontmatter has none (or cannot be read): the folder
    // name for experts/Name/expert.md, else the file name.
    private static string NameFromSource(string sourceName) =>
        Path.GetFileName(sourceName) == SourceResolver.ExpertFile && Path.GetDirectoryName(sourceName) is { Length: > 0 } dir
            ? Path.GetFileName(dir)
            : Path.GetFileNameWithoutExtension(sourceName);

    // A second file declaring an already-loaded name; points at that file's "name:" line.
    private static ExpertLoadException DuplicateNameError(string name, string firstPath, string duplicatePath)
    {
        var (frontmatter, startLine) = ReadFrontmatter(duplicatePath);
        var (line, col, endCol) = FindField(frontmatter, "name", startLine);
        return new ExpertLoadException(
            $"Duplicate expert name '{name}' in {duplicatePath}; already defined in {firstPath}.",
            duplicatePath, line, col, endCol);
    }

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
            .SelectMany(m => GetStepNames(m.Pipeline))
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
            foreach (var step in GetSteps(mission.Pipeline))
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
        foreach (var step in GetSteps(mission.Pipeline))
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
        if (markdownByName is null) return ReadFrontmatter(path);
        if (!markdownByName.TryGetValue(name, out var markdown))
            throw new ExpertLoadException($"Missing immutable diagnostic source for expert '{name}'.");
        var (text, _, startLine) = SplitFrontmatter(path, markdown);
        return (text, startLine);
    }

    // Read just the frontmatter block from an expert.md, returning (text, startLine).
    private static (string text, int startLine) ReadFrontmatter(string path)
    {
        if (!File.Exists(path)) return ("", 1);
        var lines = File.ReadAllLines(path);
        var close = ClosingDelimiterIndex(lines);
        if (close < 0) return ("", 1);
        return (string.Join('\n', lines[1..close]), 2);
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
        return FindField(frontmatter.text, blockName, frontmatter.startLine);
    }

    private static IEnumerable<string> GetStepNames(Pipeline pipeline)
        => GetSteps(pipeline).Select(s => s.ExpertName);

    private static IEnumerable<Step> GetSteps(Pipeline pipeline)
        => pipeline.Elements.SelectMany(e => e switch
        {
            StepElement se     => (IEnumerable<Step>)[se.Step],
            ParallelElement pe => pe.Steps,
            _                  => Enumerable.Empty<Step>()
        });

    private sealed record MissionReferenceEdge(string FromMission, string ToMission, Step Step);

    internal static ExpertDefinition ParseFile(string path) => ParseContent(path, File.ReadAllText(path));

    /// <summary>
    /// Parses one expert definition supplied as immutable value content. Hosts that admit an
    /// already-resolved package use this rather than materialising a temporary expert directory;
    /// Core remains the single YAML/frontmatter parser.
    /// </summary>
    public static ExpertDefinition ParseContent(string sourceName, string content)
    {
        var (frontmatter, body, frontmatterStartLine) = SplitFrontmatter(sourceName, content);

        ExpertFrontmatter meta;
        try
        {
            meta = Yaml.Deserialize<ExpertFrontmatter>(frontmatter);
        }
        catch (YamlException ex)
        {
            throw InvalidFrontmatter(sourceName, frontmatter, frontmatterStartLine, ex);
        }

        // Collect all semantic errors before surfacing them
        var errors = new List<ExpertLoadException>();
        AddMissingFieldErrors(errors, meta, frontmatter, frontmatterStartLine, sourceName);
        AddKindErrors(errors, meta, frontmatter, frontmatterStartLine, sourceName);
        AddTimeoutError(errors, meta, frontmatter, frontmatterStartLine, sourceName);

        if (errors.Count > 0)
            throw new AggregateExpertLoadException(errors);

        return new ExpertDefinition(meta.Name, meta.Input, meta.Output, body.Trim(), meta.Role, meta.Kind,
            meta.Endpoint, meta.Check, meta.OnFail, meta.Model, meta.Inputs, meta.OutputKey, meta.Threshold,
            meta.Command, meta.Args, meta.Timeout,
            ExpertDirectory: Path.GetDirectoryName(sourceName) ?? "",
            OutputKeys: meta.OutputKeys.Count > 0 ? meta.OutputKeys : null,
            InputKeys:  meta.InputKeys.Count  > 0 ? meta.InputKeys  : null);
    }

    // Reports the YAML parser's own problem in plain words, at the node where deserialization failed.
    // Typed deserialization can trip over a value's shape before the parser reaches a syntax error,
    // so the text is parsed on its own for the reason; frontmatter that parses cleanly holds a value
    // of the wrong type for its field.
    private static ExpertLoadException InvalidFrontmatter(
        string sourceName, string frontmatter, int startLine, YamlException deserializeError)
    {
        var reason = SyntaxError(frontmatter) ?? "value has the wrong type for its field";
        var start  = deserializeError.Start;
        return new ExpertLoadException(
            $"Invalid YAML frontmatter in expert '{NameFromSource(sourceName)}': {reason}",
            sourceName, startLine + (int)start.Line - 1, (int)start.Column - 1);
    }

    // The parser's syntax error in the frontmatter, or null when it parses cleanly. On some unclosed
    // flow collections YamlDotNet's parser throws InvalidOperationException with no message of use.
    private static string? SyntaxError(string frontmatter)
    {
        try
        {
            var parser = new YamlDotNet.Core.Parser(new StringReader(frontmatter));
            while (parser.MoveNext()) { }
            return null;
        }
        catch (YamlException ex)          { return ex.Message; }
        catch (InvalidOperationException) { return "syntax error"; }
    }

    private static readonly (string Field, Func<ExpertFrontmatter, string> Value)[] RequiredFields =
    [
        ("name",   meta => meta.Name),
        ("input",  meta => meta.Input),
        ("output", meta => meta.Output),
    ];

    // Fields a kind requires, in diagnostic order. Each check runs only for its own kind.
    private static readonly (string Kind, string Field, Func<ExpertFrontmatter, bool> IsMissing)[] KindRequiredFields =
    [
        (ExpertKinds.Http, "endpoint",  meta => string.IsNullOrWhiteSpace(meta.Endpoint)),
        (ExpertKinds.Rule, "check",     meta => string.IsNullOrWhiteSpace(meta.Check)),
        (ExpertKinds.Onnx, "model",     meta => string.IsNullOrWhiteSpace(meta.Model)),
        (ExpertKinds.Onnx, "inputs",    meta => meta.Inputs.Count == 0),
        (ExpertKinds.Onnx, "outputKey", meta => string.IsNullOrWhiteSpace(meta.OutputKey)),
        (ExpertKinds.Onnx, "threshold", meta => string.IsNullOrWhiteSpace(meta.Threshold)),
        (ExpertKinds.Exec, "command",   meta => string.IsNullOrWhiteSpace(meta.Command)),
        (ExpertKinds.Exec, "inputs",    meta => meta.Inputs.Count == 0),
        (ExpertKinds.Exec, "outputKey", meta => string.IsNullOrWhiteSpace(meta.OutputKey)),
    ];

    private static void AddMissingFieldErrors(
        List<ExpertLoadException> errors, ExpertFrontmatter meta, string frontmatter, int startLine, string sourceName)
    {
        foreach (var (field, value) in RequiredFields)
        {
            if (!string.IsNullOrWhiteSpace(value(meta))) continue;
            var (l, c, ec) = FindField(frontmatter, field, startLine);
            errors.Add(new ExpertLoadException(
                $"Missing required frontmatter field '{field}' in {Path.GetFileName(sourceName)}", sourceName, l, c, ec));
        }
    }

    // Kind errors point at the "kind:" line and name the expert, falling back to the file name.
    // An unknown or wrong-case kind is one error; its kind-specific fields are not checked.
    private static void AddKindErrors(
        List<ExpertLoadException> errors, ExpertFrontmatter meta, string frontmatter, int startLine, string sourceName)
    {
        var expertName = string.IsNullOrWhiteSpace(meta.Name) ? NameFromSource(sourceName) : meta.Name;
        var (line, col, endCol) = FindField(frontmatter, "kind", startLine);

        if (!ExpertKinds.IsKnown(meta.Kind))
        {
            errors.Add(new ExpertLoadException(
                $"Expert '{expertName}' has unknown kind '{meta.Kind}'. Allowed kinds: {string.Join(", ", ExpertKinds.All)}.",
                sourceName, line, col, endCol));
            return;
        }

        foreach (var (kind, field, isMissing) in KindRequiredFields)
        {
            if (meta.Kind != kind || !isMissing(meta)) continue;
            errors.Add(new ExpertLoadException(
                $"Expert '{expertName}' has kind:{kind} but is missing required field '{field}'.", sourceName, line, col, endCol));
        }
    }

    // An unparseable timeout points at the "timeout:" line; a blank one keeps the manifest default.
    private static void AddTimeoutError(
        List<ExpertLoadException> errors, ExpertFrontmatter meta, string frontmatter, int startLine, string sourceName)
    {
        if (ExecTimeout.TryParse(meta.Timeout, out _)) return;
        var expertName = string.IsNullOrWhiteSpace(meta.Name) ? NameFromSource(sourceName) : meta.Name;
        var (line, col, endCol) = FindField(frontmatter, "timeout", startLine);
        errors.Add(new ExpertLoadException(
            $"Expert '{expertName}' has invalid timeout '{meta.Timeout}'. Use <int>s or <int>m, e.g. 30s or 2m.",
            sourceName, line, col, endCol));
    }

    // Returns (frontmatter text, body text, 1-based line number of the first frontmatter line in the file).
    private static (string Frontmatter, string Body, int FrontmatterStartLine) SplitFrontmatter(string path, string content)
    {
        var lines = content.Split('\n');

        if (lines.Length < 2 || lines[0].Trim() != FrontmatterDelimiter)
            throw new ExpertLoadException(
                $"Missing frontmatter delimiter '---' at start of {Path.GetFileName(path)}", path, 1, 0, 3);

        var closingIndex = ClosingDelimiterIndex(lines);
        if (closingIndex < 0)
            throw new ExpertLoadException(
                $"Unclosed frontmatter block in {Path.GetFileName(path)}", path, 1, 0, 3);

        var frontmatter = string.Join('\n', lines[1..closingIndex]);
        var body        = string.Join('\n', lines[(closingIndex + 1)..]);
        return (frontmatter, body, FrontmatterStartLine: 2); // line 1 is "---", fields start at line 2
    }

    // Index of the first "---" line after line 0, or -1 when the block is never closed.
    private static int ClosingDelimiterIndex(string[] lines)
    {
        for (int i = 1; i < lines.Length; i++)
            if (lines[i].Trim() == FrontmatterDelimiter) return i;
        return -1;
    }

    // Scans frontmatter text for "fieldName:" and returns its file-absolute line/col span.
    // When the field is absent, points at the opening "---" delimiter (startLine - 1).
    private static (int line, int col, int endCol) FindField(string frontmatter, string fieldName, int startLine)
    {
        var lines = frontmatter.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var raw     = lines[i];
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith(fieldName + ":", StringComparison.Ordinal))
            {
                var col = raw.Length - trimmed.Length;
                return (startLine + i, col, col + fieldName.Length);
            }
        }
        // Field not present — point at the opening "---" delimiter
        return (startLine - 1, 0, 3);
    }

    private class ExpertFrontmatter
    {
        public string       Name      { get; set; } = "";
        public string       Input     { get; set; } = "";
        public string       Output    { get; set; } = "";
        public string       Role      { get; set; } = "";
        public string       Kind      { get; set; } = ExpertKinds.Llm;
        public string       Endpoint  { get; set; } = "";
        public string       Check     { get; set; } = "";
        public string       OnFail    { get; set; } = "";
        public string       Model     { get; set; } = "";
        public List<string> Inputs    { get; set; } = [];
        public string       OutputKey { get; set; } = "";
        public string       Threshold { get; set; } = "";
        public string       Command     { get; set; } = "";
        public List<string> Args        { get; set; } = [];
        public string       Timeout     { get; set; } = "";
        public Dictionary<string, string> OutputKeys { get; set; } = [];
        public Dictionary<string, string> InputKeys  { get; set; } = [];
    }
}
