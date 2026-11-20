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

    // Read just the frontmatter block from an expert.md, returning (text, startLine).
    internal static (string text, int startLine) ReadFrontmatter(string path)
    {
        if (!File.Exists(path)) return ("", 1);
        var lines = File.ReadAllLines(path);
        var close = ClosingDelimiterIndex(lines);
        if (close < 0) return ("", 1);
        return (string.Join('\n', lines[1..close]), 2);
    }

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
    internal static (string Frontmatter, string Body, int FrontmatterStartLine) SplitFrontmatter(string path, string content)
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
    internal static int ClosingDelimiterIndex(string[] lines)
    {
        for (int i = 1; i < lines.Length; i++)
            if (lines[i].Trim() == FrontmatterDelimiter) return i;
        return -1;
    }

    // Scans frontmatter text for "fieldName:" and returns its file-absolute line/col span.
    // When the field is absent, points at the opening "---" delimiter (startLine - 1).
    internal static (int line, int col, int endCol) FindField(string frontmatter, string fieldName, int startLine)
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
