namespace ForgeMission.Core.Manifest;

// Minimal TOML reader for forge.toml — handles only the schema we need:
//   [section] and [section.name] headers
//   key = "string value"
//   key = env("VAR") or env("VAR", "default")
//   key = ["string", "array"]
//   key = 100
//   key = true
// No inline tables or general TOML semantics — TOML is a superset of what we parse.
public static class ForgeTomlReader
{
    public static readonly string FileName = "forge.toml";

    public static ForgeManifest? TryRead(string missionFilePath) => Read(missionFilePath, distributionOnly: false);

    public static ForgeManifest? TryReadDistribution(string missionFilePath) => Read(missionFilePath, distributionOnly: true);

    private static ForgeManifest? Read(string missionFilePath, bool distributionOnly)
    {
        var dir      = Path.GetDirectoryName(Path.GetFullPath(missionFilePath))!;
        var tomlPath = Path.Combine(dir, FileName);

        if (!File.Exists(tomlPath))
            return null;

        var lines = File.ReadAllLines(tomlPath);
        return Parse(lines, tomlPath, distributionOnly);
    }

    private static ForgeManifest Parse(string[] lines, string path, bool distributionOnly)
    {
        var rows = ReadRows(lines, path, distributionOnly);
        return new ForgeManifest
        {
            Experts = rows.Experts,
            Providers = BuildProviders(rows.Profiles, path),
            Execution = BuildExecution(rows.Execution, path),
            Capabilities = new CapabilityConfig { Artifacts = BuildArtifactCapabilities(rows.Inputs, rows.Modes, path) },
            Package = new PackageConfig(rows.Assets),
        };
    }

    private static ManifestRows ReadRows(string[] lines, string path, bool distributionOnly)
    {
        var rows = new ManifestRows();
        var section = new ManifestSection("");
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Split('#')[0].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                section = ReadSection(line, rows, distributionOnly, index + 1, path);
                continue;
            }
            if (distributionOnly && section.Name is not ("experts" or "package")) continue;
            var (key, value) = ReadAssignment(line, lines, ref index, section, distributionOnly, path);
            AddRow(rows, section, key, value, index + 1, path);
        }
        return rows;
    }

    private static ManifestSection ReadSection(string line, ManifestRows rows, bool distributionOnly, int number, string path)
    {
        if (!line.EndsWith(']')) throw new ForgeTomlException($"Line {number}: malformed section header", path);
        var header = line[1..^1].Trim();
        if (distributionOnly) return new(header);
        if (header.StartsWith("providers.", StringComparison.Ordinal))
        {
            var name = SectionName(header, "providers.", "provider", number, path);
            rows.Profiles[name] = new(StringComparer.Ordinal);
            return new("providers", name);
        }
        if (header.StartsWith("capabilities.artifacts.inputs.", StringComparison.Ordinal))
        {
            var name = SectionName(header, "capabilities.artifacts.inputs.", "artifact input", number, path);
            rows.Inputs[name] = new(StringComparer.Ordinal);
            return new("artifactInput", name);
        }
        if (header.StartsWith("capabilities.artifacts.modes.", StringComparison.Ordinal))
        {
            var name = SectionName(header, "capabilities.artifacts.modes.", "artifact mode", number, path);
            rows.Modes[name] = new(StringComparer.Ordinal);
            return new("artifactMode", name);
        }
        return new(header);
    }

    private static string SectionName(string header, string prefix, string kind, int number, string path)
    {
        var name = header[prefix.Length..].Trim();
        if (name.Length == 0) throw new ForgeTomlException($"Line {number}: empty {kind} name", path);
        return name;
    }

    private static (string Key, TomlValue Value) ReadAssignment(string line, string[] lines, ref int index,
        ManifestSection section, bool distributionOnly, string path)
    {
        var equals = line.IndexOf('=');
        if (equals <= 0) throw new ForgeTomlException($"Line {index + 1}: expected key = value", path);
        var raw = line[(equals + 1)..].Trim();
        if (raw.StartsWith('[') && !raw.EndsWith(']')) raw = ReadMultilineArray(raw, lines, ref index, path);
        if ((distributionOnly || section.Name == "package") && raw.StartsWith("env(", StringComparison.Ordinal))
            throw new ForgeTomlException($"Line {index + 1}: distribution metadata must be literal", path);
        return (line[..equals].Trim(), ResolveValue(raw, index + 1, path));
    }

    private static void AddRow(ManifestRows rows, ManifestSection section, string key, TomlValue value, int number, string path)
    {
        switch (section.Name)
        {
            case "experts": rows.Experts[key] = value.AsString(number, path); break;
            case "package":
                if (key != "assets") throw new ForgeTomlException($"[package] unknown field \"{key}\"", path);
                rows.Assets = value.AsStringArray("[package].assets", path);
                break;
            case "providers" when section.Key is not null:
                rows.Profiles[section.Key][key] = value.AsString(number, path);
                break;
            case "execution":
                if (key is not ("backend" or "defaultTimeout")) throw new ForgeTomlException($"[execution] unknown field \"{key}\"", path);
                rows.Execution[key] = value.AsString(number, path);
                break;
            case "artifactInput" when section.Key is not null:
                AddKnownArtifactField(rows.Inputs[section.Key], key, value, number, path, ["content_types", "max_size_mb"]);
                break;
            case "artifactMode" when section.Key is not null:
                AddKnownArtifactField(rows.Modes[section.Key], key, value, number, path, ["output_content_type", "output_extension", "default"]);
                break;
        }
    }

    private static Dictionary<string, ProviderProfile> BuildProviders(Dictionary<string, Dictionary<string, string>> profiles, string path)
    {
        var providers = new Dictionary<string, ProviderProfile>(StringComparer.Ordinal);
        foreach (var (name, rows) in profiles)
        {
            AssertField(rows, "provider", $"[providers.{name}]", path);
            AssertField(rows, "model", $"[providers.{name}]", path);
            var knownProviders = new[] { "openai", "anthropic", "azure", "ollama", "xai" };
            if (!knownProviders.Contains(rows["provider"]))
                throw new ForgeTomlException($"[providers.{name}] provider \"{rows["provider"]}\" is not recognised. Known providers: {string.Join(", ", knownProviders)}", path);
            ValidateProviderFields(rows, name, path);
            providers[name] = new ProviderProfile { Provider = rows["provider"], Model = rows["model"],
                ApiKey = rows.GetValueOrDefault("apiKey"), Endpoint = rows.GetValueOrDefault("endpoint") };
        }
        return providers;
    }

    private static void ValidateProviderFields(Dictionary<string, string> rows, string name, string path)
    {
        foreach (var key in rows.Keys)
            if (key is not ("provider" or "model" or "apiKey" or "endpoint"))
                throw new ForgeTomlException($"[providers.{name}] unknown field \"{key}\"", path);
    }

    private static ExecutionConfig BuildExecution(Dictionary<string, string> rows, string path)
    {
        if (rows.TryGetValue("backend", out var backend) && backend != "process")
            throw new ForgeTomlException($"[execution] backend \"{backend}\" is not recognised. Known backends: process", path);
        return new ExecutionConfig { Backend = rows.GetValueOrDefault("backend", "process"),
            DefaultTimeout = rows.GetValueOrDefault("defaultTimeout", "30s") };
    }

    private sealed record ManifestSection(string Name, string? Key = null);
    private sealed class ManifestRows
    {
        internal Dictionary<string, string> Experts { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, Dictionary<string, string>> Profiles { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> Execution { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, Dictionary<string, TomlValue>> Inputs { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, Dictionary<string, TomlValue>> Modes { get; } = new(StringComparer.Ordinal);
        internal IReadOnlyList<string> Assets { get; set; } = [];
    }
    // Parses "string value", env("VAR") or env("VAR", "default"), strips surrounding quotes.
    private static TomlValue ResolveValue(string raw, int lineNum, string path)
    {
        if (raw.StartsWith("env(", StringComparison.Ordinal))
        {
            if (!raw.EndsWith(')'))
                throw new ForgeTomlException($"Line {lineNum}: malformed env() call", path);

            var inner   = raw[4..^1];
            var parts   = SplitArgs(inner);
            var varName = parts[0].Trim().Trim('"');
            var def     = parts.Length > 1 ? parts[1].Trim().Trim('"') : null;

            return TomlValue.String(Environment.GetEnvironmentVariable(varName) ?? def ?? string.Empty);
        }

        if (raw.StartsWith('"') && raw.EndsWith('"') && raw.Length >= 2)
            return TomlValue.String(raw[1..^1]);

        if (raw.StartsWith('[') && raw.EndsWith(']'))
            return TomlValue.StringArray(ParseStringArray(raw, lineNum, path));

        if (int.TryParse(raw, out var i))
            return TomlValue.Int(i);

        if (raw is "true" or "false")
            return TomlValue.Bool(raw == "true");

        throw new ForgeTomlException(
            $"Line {lineNum}: value must be a quoted string, env() call, string array, integer, or boolean", path);
    }

    private static ArtifactCapabilities BuildArtifactCapabilities(
        Dictionary<string, Dictionary<string, TomlValue>> inputRows,
        Dictionary<string, Dictionary<string, TomlValue>> modeRows,
        string path)
    {
        var inputs = new Dictionary<string, ArtifactInputCapability>(StringComparer.Ordinal);
        foreach (var (name, rows) in inputRows)
        {
            Require(rows, "content_types", $"[capabilities.artifacts.inputs.{name}]", path);
            Require(rows, "max_size_mb", $"[capabilities.artifacts.inputs.{name}]", path);

            inputs[name] = new ArtifactInputCapability
            {
                ContentTypes = rows["content_types"].AsStringArray($"[capabilities.artifacts.inputs.{name}].content_types", path),
                MaxSizeMb = rows["max_size_mb"].AsInt($"[capabilities.artifacts.inputs.{name}].max_size_mb", path),
            };
        }

        var modes = new Dictionary<string, ArtifactModeCapability>(StringComparer.Ordinal);
        foreach (var (name, rows) in modeRows)
        {
            Require(rows, "output_content_type", $"[capabilities.artifacts.modes.{name}]", path);
            Require(rows, "output_extension", $"[capabilities.artifacts.modes.{name}]", path);

            modes[name] = new ArtifactModeCapability
            {
                OutputContentType = rows["output_content_type"].AsString($"[capabilities.artifacts.modes.{name}].output_content_type", path),
                OutputExtension = rows["output_extension"].AsString($"[capabilities.artifacts.modes.{name}].output_extension", path),
                Default = rows.TryGetValue("default", out var d)
                    && d.AsBool($"[capabilities.artifacts.modes.{name}].default", path),
            };
        }

        return new ArtifactCapabilities { Inputs = inputs, Modes = modes };
    }

    private static void AddKnownArtifactField(
        Dictionary<string, TomlValue> rows,
        string key,
        TomlValue value,
        int lineNum,
        string path,
        string[] known)
    {
        if (!known.Contains(key))
            throw new ForgeTomlException($"Line {lineNum}: unknown artifact capability field \"{key}\"", path);
        rows[key] = value;
    }

    private static string ReadMultilineArray(string firstLine, string[] lines, ref int index, string path)
    {
        var parts = new List<string> { firstLine };
        while (++index < lines.Length)
        {
            var line = lines[index].Split('#')[0].Trim();
            parts.Add(line);
            if (line.EndsWith(']')) return string.Join(" ", parts);
        }

        throw new ForgeTomlException("Unterminated array value", path);
    }

    private static string[] ParseStringArray(string raw, int lineNum, string path)
    {
        var inner = raw[1..^1].Trim();
        if (inner.Length == 0) return [];

        var values = SplitArgs(inner)
            .Select(v => v.Trim().TrimEnd(',').Trim())
            .Where(v => v.Length > 0)
            .Select(v =>
            {
                if (!v.StartsWith('"') || !v.EndsWith('"') || v.Length < 2)
                    throw new ForgeTomlException($"Line {lineNum}: array values must be quoted strings", path);
                return v[1..^1];
            })
            .ToArray();
        return values;
    }

    private static string[] SplitArgs(string s)
    {
        var results = new List<string>();
        var depth   = 0;
        var start   = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') depth = 1 - depth;
            if (s[i] == ',' && depth == 0)
            {
                results.Add(s[start..i]);
                start = i + 1;
            }
        }
        results.Add(s[start..]);
        return [.. results];
    }

    private static void AssertField(Dictionary<string, string> rows, string key, string context, string path)
    {
        if (!rows.ContainsKey(key) || rows[key].Length == 0)
            throw new ForgeTomlException($"{context} missing required field \"{key}\"", path);
    }

    private static void Require(Dictionary<string, TomlValue> rows, string key, string context, string path)
    {
        if (!rows.ContainsKey(key))
            throw new ForgeTomlException($"{context} missing required field \"{key}\"", path);
    }

    private readonly record struct TomlValue(
        string? StringValue,
        string[]? StringArrayValue,
        int? IntValue,
        bool? BoolValue)
    {
        public static TomlValue String(string value) => new(value, null, null, null);
        public static TomlValue StringArray(string[] value) => new(null, value, null, null);
        public static TomlValue Int(int value) => new(null, null, value, null);
        public static TomlValue Bool(bool value) => new(null, null, null, value);

        public string AsString(int lineNum, string path) =>
            StringValue ?? throw new ForgeTomlException($"Line {lineNum}: value must be a quoted string or env() call", path);

        public string AsString(string context, string path) =>
            StringValue ?? throw new ForgeTomlException($"{context} must be a quoted string", path);

        public string[] AsStringArray(string context, string path) =>
            StringArrayValue ?? throw new ForgeTomlException($"{context} must be a string array", path);

        public int AsInt(string context, string path) =>
            IntValue ?? throw new ForgeTomlException($"{context} must be an integer", path);

        public bool AsBool(string context, string path) =>
            BoolValue ?? throw new ForgeTomlException($"{context} must be a boolean", path);
    }
}
