using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeMission.Core.Experts;
using ForgeMission.Parser;
using MclProgram = ForgeMission.Parser.Program;

namespace ForgeMission.Core.Runtime;

/// <summary>The single pure constructor and validator for resolved durable mission content.</summary>
public static class DurableMissionPackageValidator
{
    public const int CurrentFormatVersion = 1;
    public const int MaxPackageUtf8Bytes = 4 * 1024 * 1024;
    public const string DefaultProviderProfile = "default";

    public static bool TryCreate(string missionSource, IReadOnlyList<DurableResolvedExpertInput> resolvedExperts,
        IReadOnlyList<DurableMissionAssetInput> assets, out DurableMissionPackageInput? package, out string? reason)
    {
        package = null;
        reason = null;
        try
        {
            var root = MclParser.Parse(missionSource).Declarations.OfType<MissionDeclaration>().FirstOrDefault();
            if (root is null) { reason = "The durable package has no root mission."; return false; }
            var raw = new DurableMissionPackageInput(CurrentFormatVersion, "", missionSource, root.Name,
                root.Params.FirstOrDefault() ?? "", resolvedExperts, assets);
            if (!ValidShape(raw)) { reason = "The durable package shape is invalid."; return false; }
            raw = raw with { PackageHash = ComputeHash(raw) };
            if (!TryValidate(raw, out _, out reason)) return false;
            package = raw;
            return true;
        }
        catch (ParseException exception) { reason = $"The durable package could not be parsed: {exception.Message}"; return false; }
    }

    public static bool TryValidate(DurableMissionPackageInput package,
        out ValidatedDurableMissionPackage? validated, out string? reason)
    {
        validated = null;
        reason = null;
        if (!ValidShape(package)) { reason = "The durable package shape is invalid."; return false; }
        if (!string.Equals(package.PackageHash, ComputeHash(package), StringComparison.OrdinalIgnoreCase))
        { reason = "The durable package hash does not match its canonical content."; return false; }
        if (JsonSerializer.SerializeToUtf8Bytes(package, DurablePackageJsonContext.Default.DurableMissionPackageInput).Length > MaxPackageUtf8Bytes)
        { reason = "The serialized durable package exceeds 4 MiB."; return false; }
        try
        {
            var experts = ParseExperts(package);
            var ast = MclParser.Parse(package.MissionSource);
            ExpertLoader.Validate(ast, experts, contractErrorsAreFatal: true, missionFilePath: "mission.mcl",
                expertMarkdownByName: package.ResolvedExperts.ToDictionary(e => e.Name, e => e.ExpertMarkdown, StringComparer.Ordinal));
            ValidateSemantics(package, ast, experts);
            ValidateAssets(package, experts);
            var names = DurableMissionInputPolicy.AdmittedNames(ast, experts, package.RootMissionName);
            var profiles = DurableMissionInputPolicy.ReachableSteps(ast, package.RootMissionName)
                .Where(step => experts.TryGetValue(step.ExpertName, out var expert) && expert.Kind == "llm")
                .Select(step => step.Using ?? DefaultProviderProfile).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            validated = new(package, ast, experts, names, profiles);
            return true;
        }
        catch (Exception exception) when (exception is ExpertLoadException or AggregateExpertLoadException
            or ParseException or InvalidOperationException or ArgumentException)
        { reason = $"The durable package is invalid: {exception.Message}"; return false; }
    }

    public static bool TryValidateInputNames(ValidatedDurableMissionPackage package,
        IReadOnlyCollection<string> names, out string? reason)
    {
        reason = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
            if (!DurableMissionInputPolicy.IsIdentifier(name) || DurableMissionInputPolicy.IsReserved(name)
                || !seen.Add(name) || !package.AdmittedInputNames.Contains(name, StringComparer.Ordinal))
            { reason = $"Input name '{name}' is invalid, reserved, duplicated or undeclared."; return false; }
        var root = package.Ast.Declarations.OfType<MissionDeclaration>().First();
        if (root.Params.Any(parameter => !seen.Contains(parameter)))
        { reason = "All root mission parameters must be supplied explicitly."; return false; }
        return true;
    }

    public static string ComputeHash(DurableMissionPackageInput package)
    {
        var canonical = new StringBuilder();
        Append(canonical, package.FormatVersion.ToString(CultureInfo.InvariantCulture));
        Append(canonical, package.MissionSource); Append(canonical, package.RootMissionName); Append(canonical, package.RootInputName);
        foreach (var expert in package.ResolvedExperts.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            Append(canonical, expert.Name); Append(canonical, expert.LockSource); Append(canonical, expert.LockPath);
            Append(canonical, expert.LockHash); Append(canonical, expert.ExpertMarkdown);
        }
        if (package.Assets is { Count: > 0 } assets)
        {
            Append(canonical, "assets"); Append(canonical, assets.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var asset in assets.OrderBy(a => a.Path, StringComparer.Ordinal))
            {
                Append(canonical, asset.Path); Append(canonical, asset.ContentType);
                Append(canonical, asset.Bytes.Length.ToString(CultureInfo.InvariantCulture));
                Append(canonical, asset.Sha256.ToLowerInvariant()); Append(canonical, asset.Executable ? "1" : "0");
            }
        }
        return Hash(Encoding.UTF8.GetBytes(canonical.ToString()));
    }

    private static bool ValidShape(DurableMissionPackageInput? package) => package is not null
        && package.FormatVersion == CurrentFormatVersion && !string.IsNullOrWhiteSpace(package.MissionSource)
        && !string.IsNullOrWhiteSpace(package.RootMissionName) && package.RootInputName is not null
        && package.PackageHash is not null && package.ResolvedExperts is not null
        && package.ResolvedExperts.All(ValidExpertShape) && (package.Assets ?? []).All(ValidAssetShape)
        && MinimumContentBytes(package) <= MaxPackageUtf8Bytes;

    private static bool ValidExpertShape(DurableResolvedExpertInput? expert) => expert is not null
        && !string.IsNullOrWhiteSpace(expert.Name) && !string.IsNullOrWhiteSpace(expert.LockSource)
        && !string.IsNullOrWhiteSpace(expert.LockPath) && expert.LockHash is not null && expert.ExpertMarkdown is not null;

    private static bool ValidAssetShape(DurableMissionAssetInput? asset) => asset is not null
        && asset.Path is not null && !string.IsNullOrWhiteSpace(asset.ContentType) && asset.Bytes is not null && asset.Sha256 is not null;

    // A cheap lower bound avoids allocating serialization buffers for already-oversized values.
    // The source-generated serialization above remains the authoritative JSON/base64 measurement.
    private static long MinimumContentBytes(DurableMissionPackageInput package) => (long)package.MissionSource.Length
        + package.RootMissionName.Length + package.RootInputName.Length + package.PackageHash.Length
        + package.ResolvedExperts.Sum(e => (long)e.Name.Length + e.LockSource.Length + e.LockPath.Length + e.LockHash.Length + e.ExpertMarkdown.Length)
        + (package.Assets ?? []).Sum(a => (long)a.Path.Length + a.ContentType.Length + a.Sha256.Length + a.Bytes.LongLength);

    private static Dictionary<string, ExpertDefinition> ParseExperts(DurableMissionPackageInput package)
    {
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal);
        foreach (var entry in package.ResolvedExperts)
        {
            if (!IsHash(entry.LockHash, Encoding.UTF8.GetBytes(entry.ExpertMarkdown)))
                throw new InvalidOperationException("Resolved expert content hash does not match.");
            var expert = ExpertLoader.ParseContent($"experts/{entry.Name}/expert.md", entry.ExpertMarkdown);
            if (!DurableMissionInputPolicy.IsIdentifier(entry.Name) || expert.Name != entry.Name || !experts.TryAdd(entry.Name, expert))
                throw new InvalidOperationException("Resolved expert name is inconsistent or duplicated.");
        }
        return experts;
    }

    private static void ValidateSemantics(DurableMissionPackageInput package, MclProgram ast,
        Dictionary<string, ExpertDefinition> experts)
    {
        var missions = ast.Declarations.OfType<MissionDeclaration>().ToArray();
        var root = missions.FirstOrDefault();
        if (root is null || root.Name != package.RootMissionName || (root.Params.FirstOrDefault() ?? "") != package.RootInputName)
            throw new InvalidOperationException("Root metadata must select the first mission and its first parameter.");
        if (ast.Bindings.Any(b => b.Value is EnvLetValue || DurableMissionInputPolicy.IsReserved(b.Name))
            || missions.SelectMany(m => m.Params).Any(DurableMissionInputPolicy.IsReserved)
            || missions.SelectMany(m => DurableMissionInputPolicy.Steps(m.Pipeline)).SelectMany(s => s.Context)
                .Any(b => b.Value is EnvBindingValue || DurableMissionInputPolicy.IsReserved(b.Key)))
            throw new InvalidOperationException("Environment expressions and reserved authored bindings are not admitted.");
        foreach (var expert in experts.Values)
        {
            if (expert.Kind is not ("llm" or "rule" or "json_extract" or "exec" or "onnx" or "http" or "search"))
                throw new InvalidOperationException($"Unknown expert kind '{expert.Kind}'.");
            if ((expert.Inputs ?? []).Any(name => !DurableMissionInputPolicy.IsIdentifier(name)
                || (DurableMissionInputPolicy.IsReserved(name) && !DurableMissionInputPolicy.IsRuntimeDirectory(name))))
                throw new InvalidOperationException("Expert inputs contain invalid or reserved names.");
        }
    }

    private static void ValidateAssets(DurableMissionPackageInput package, Dictionary<string, ExpertDefinition> experts)
    {
        var files = new HashSet<string>(["mission.mcl", "mcl.lock", "forge.toml"], StringComparer.OrdinalIgnoreCase);
        foreach (var name in experts.Keys)
            if (!files.Add($"experts/{name}/expert.md")) throw new InvalidOperationException("Expert paths collide.");
        var assets = package.Assets ?? [];
        foreach (var asset in assets)
        {
            if (!IsCanonicalPath(asset.Path) || asset.Path.Split('/')[0].Equals("inputs", StringComparison.OrdinalIgnoreCase)
                || asset.Path.Split('/')[0].Equals("outputs", StringComparison.OrdinalIgnoreCase)
                || files.Any(path => Collides(path, asset.Path)) || !IsHash(asset.Sha256, asset.Bytes))
                throw new InvalidOperationException("Asset path, collision or content hash is invalid.");
            files.Add(asset.Path);
        }
        foreach (var expert in experts.Values.Where(e => e.Kind == "onnx"))
            if (!assets.Any(asset => asset.Path.Equals(ResolveModelPath(expert), StringComparison.Ordinal)))
                throw new InvalidOperationException($"ONNX model for '{expert.Name}' is not an admitted asset.");
    }

    private static string ResolveModelPath(ExpertDefinition expert)
    {
        if (string.IsNullOrWhiteSpace(expert.Model) || expert.Model.StartsWith('/') || expert.Model.Contains('\\') || expert.Model.Contains(':'))
            throw new InvalidOperationException("ONNX model must be package-relative.");
        var segments = new List<string> { "experts", expert.Name };
        foreach (var segment in expert.Model.Split('/'))
        {
            if (segment == ".") continue;
            if (segment != "..") { segments.Add(segment); continue; }
            if (segments.Count == 0) throw new InvalidOperationException("ONNX model escapes the package.");
            segments.RemoveAt(segments.Count - 1);
        }
        return string.Join('/', segments);
    }

    internal static bool IsCanonicalPath(string path) => !string.IsNullOrEmpty(path)
        && !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl)
        && path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    private static bool Collides(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase)
        || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);
    private static void Append(StringBuilder target, string value) => target.Append(value.Length).Append(':').Append(value);
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsHash(string expected, byte[] bytes) => string.Equals(expected, Hash(bytes), StringComparison.OrdinalIgnoreCase);
}

public sealed record DurableResolvedExpertInput(string Name, string LockSource, string LockPath, string LockHash, string ExpertMarkdown);
public sealed record DurableMissionAssetInput(string Path, string ContentType, byte[] Bytes, string Sha256, bool Executable = false);
[method: JsonConstructor]
public sealed record DurableMissionPackageInput(int FormatVersion, string PackageHash, string MissionSource,
    string RootMissionName, string RootInputName, IReadOnlyList<DurableResolvedExpertInput> ResolvedExperts,
    IReadOnlyList<DurableMissionAssetInput>? Assets = null)
{
    public DurableMissionPackageInput(int FormatVersion, string PackageHash, string MissionSource,
        string RootMissionName, string RootInputName, IReadOnlyList<DurableResolvedExpertInput> ResolvedExperts)
        : this(FormatVersion, PackageHash, MissionSource, RootMissionName, RootInputName, ResolvedExperts, null) { }
}
public sealed record ValidatedDurableMissionPackage(DurableMissionPackageInput Input, MclProgram Ast,
    Dictionary<string, ExpertDefinition> Experts, IReadOnlyList<string> AdmittedInputNames, IReadOnlyList<string> ProviderProfileNames);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DurableMissionPackageInput))]
internal partial class DurablePackageJsonContext : JsonSerializerContext;
