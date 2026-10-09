using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeMission.Core.Experts;
using ForgeMission.Parser;
using MclProgram = ForgeMission.Parser.Program;

namespace ForgeMission.Core.Runtime;

/// <summary>
/// Validates the bounded value content needed to execute a durable package.  It is intentionally
/// independent of Conversation contracts so both a Host admission boundary and a Worker can map
/// their wire value into this one Core parser/validator without duplicating YAML or MCL parsing.
/// </summary>
public static class DurableMissionPackageValidator
{
    public const int CurrentFormatVersion = 1;
    /// <summary>Packages are staged as one bounded Host body, not embedded in a command.</summary>
    public const int MaxPackageUtf8Bytes = 4 * 1024 * 1024;

    /// <summary>The profile an llm step runs on when it omits <c>using</c>.</summary>
    public const string DefaultProviderProfile = "default";

    /// <summary>The only names a durable step may select with <c>using</c>. Core owns the names so
    /// every validator (Host admission, runner, client) agrees without configuration; each
    /// deployment owns what a name is bound to (provider, model, key) and must bind every one.</summary>
    public static IReadOnlySet<string> ProviderProfiles { get; } =
        new HashSet<string>(["anthropic"], StringComparer.Ordinal);

    public static bool TryValidate(
        DurableMissionPackageInput package,
        out ValidatedDurableMissionPackage? validated,
        out string? reason)
    {
        validated = null;
        reason = null;
        if (package.FormatVersion != CurrentFormatVersion || string.IsNullOrWhiteSpace(package.MissionSource) ||
            string.IsNullOrWhiteSpace(package.RootMissionName) || string.IsNullOrWhiteSpace(package.RootInputName) ||
            package.ResolvedExperts is null || package.ResolvedExperts.Count == 0 ||
            PackageContentUtf8Bytes(package) > MaxPackageUtf8Bytes)
        {
            reason = "The durable package shape is invalid.";
            return false;
        }

        if (!string.Equals(package.PackageHash, ComputeHash(package), StringComparison.OrdinalIgnoreCase))
        {
            reason = "The durable package hash does not match its canonical content.";
            return false;
        }

        try
        {
            var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal);
            foreach (var entry in package.ResolvedExperts.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.LockSource) ||
                    string.IsNullOrWhiteSpace(entry.LockPath) ||
                    !IsContentHash(entry.LockHash, entry.ExpertMarkdown) || !experts.TryAdd(entry.Name,
                        ExpertLoader.ParseContent($"durable/{entry.Name}/expert.md", entry.ExpertMarkdown)))
                {
                    reason = "The durable package contains invalid or inconsistent resolved expert content.";
                    return false;
                }
                if (!string.Equals(experts[entry.Name].Name, entry.Name, StringComparison.Ordinal))
                {
                    reason = "A resolved expert name does not match its markdown definition.";
                    return false;
                }
            }

            var ast = MclParser.Parse(package.MissionSource);
            ExpertLoader.Validate(ast, experts, warnings: null, contractErrorsAreFatal: true, missionFilePath: "durable/mission.mcl");
            var root = ast.Declarations.OfType<MissionDeclaration>().SingleOrDefault(m => m.Name == package.RootMissionName);
            if (root is null || !root.Params.Contains(package.RootInputName, StringComparer.Ordinal))
            {
                reason = "The durable package root mission or root input is invalid.";
                return false;
            }

            var steps = ast.Declarations.OfType<MissionDeclaration>().SelectMany(m => Steps(m.Pipeline)).ToArray();
            if (steps.FirstOrDefault(step => step.Using is { } name && !ProviderProfiles.Contains(name)) is { } unlisted)
            {
                reason = $"Durable packages cannot select provider profile '{unlisted.Using}'; allowed: {string.Join(", ", ProviderProfiles)}.";
                return false;
            }

            if (experts.Values.Any(expert => expert.Kind is not ("llm" or "rule" or "json_extract")))
            {
                reason = "The durable package contains an unsupported expert kind.";
                return false;
            }

            var profiles = LlmStepProfiles(steps, experts);
            if (profiles.Length > 1)
            {
                reason = "A durable package uses one provider profile for all its llm steps.";
                return false;
            }

            validated = new ValidatedDurableMissionPackage(package, ast, experts, profiles.SingleOrDefault() ?? DefaultProviderProfile);
            return true;
        }
        catch (Exception exception) when (exception is ExpertLoadException or AggregateExpertLoadException or ParseException or InvalidOperationException)
        {
            reason = $"The durable package could not be parsed: {exception.Message}";
            return false;
        }
    }

    public static string ComputeHash(DurableMissionPackageInput package)
    {
        var canonical = new StringBuilder();
        Append(canonical, package.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(canonical, package.MissionSource);
        Append(canonical, package.RootMissionName);
        Append(canonical, package.RootInputName);
        foreach (var expert in package.ResolvedExperts.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            Append(canonical, expert.Name); Append(canonical, expert.LockSource); Append(canonical, expert.LockPath);
            Append(canonical, expert.LockHash); Append(canonical, expert.ExpertMarkdown);
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    /// <summary>Writes the package transport body in the one AOT-safe canonical JSON shape.</summary>
    public static byte[] Serialize(DurableMissionPackageInput package)
    {
        if (!TryValidate(package, out _, out var reason))
            throw new ArgumentException(reason ?? "The durable package is invalid.", nameof(package));
        var canonical = package with
        {
            ResolvedExperts = package.ResolvedExperts.OrderBy(expert => expert.Name, StringComparer.Ordinal).ToArray(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(canonical, DurableMissionPackageJsonContext.Default.DurableMissionPackageInput);
    }

    /// <summary>Reads and validates a package body before it reaches provider execution.</summary>
    public static bool TryDeserialize(
        ReadOnlySpan<byte> bytes,
        out DurableMissionPackageInput? package,
        out string? reason)
    {
        package = null;
        reason = null;
        try
        {
            var parsed = JsonSerializer.Deserialize(bytes, DurableMissionPackageJsonContext.Default.DurableMissionPackageInput);
            if (parsed is null)
            {
                reason = "The durable package body is empty.";
                return false;
            }
            if (!TryValidate(parsed, out _, out reason)) return false;
            package = parsed;
            return true;
        }
        catch (JsonException)
        {
            reason = "The durable package body is not valid JSON.";
            return false;
        }
    }

    private static void Append(StringBuilder target, string value) => target.Append(value.Length).Append(':').Append(value);
    private static int PackageContentUtf8Bytes(DurableMissionPackageInput package) =>
        Encoding.UTF8.GetByteCount(package.MissionSource) + Encoding.UTF8.GetByteCount(package.RootMissionName) +
        Encoding.UTF8.GetByteCount(package.RootInputName) + package.ResolvedExperts.Sum(expert =>
            Encoding.UTF8.GetByteCount(expert.Name) + Encoding.UTF8.GetByteCount(expert.LockSource) +
            Encoding.UTF8.GetByteCount(expert.LockPath) + Encoding.UTF8.GetByteCount(expert.LockHash) +
            Encoding.UTF8.GetByteCount(expert.ExpertMarkdown));
    private static bool IsContentHash(string expected, string content) =>
        expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected["sha256:".Length..], Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), StringComparison.OrdinalIgnoreCase);
    // One profile per package keeps the run's model exact for settlement. Only llm steps select a
    // runner; a step that names a sub-mission or a non-llm expert is not counted.
    private static string[] LlmStepProfiles(IEnumerable<Step> steps, Dictionary<string, ExpertDefinition> experts) =>
        steps.Where(step => experts.TryGetValue(step.ExpertName, out var expert) && expert.Kind == "llm")
            .Select(step => step.Using ?? DefaultProviderProfile)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    private static IEnumerable<Step> Steps(Pipeline pipeline) => pipeline.Elements.SelectMany(element => element switch
    {
        StepElement step => (IEnumerable<Step>)[step.Step],
        ParallelElement parallel => parallel.Steps,
        _ => [],
    });
}

public sealed record DurableResolvedExpertInput(string Name, string LockSource, string LockPath, string LockHash, string ExpertMarkdown);
public sealed record DurableMissionPackageInput(int FormatVersion, string PackageHash, string MissionSource,
    string RootMissionName, string RootInputName, IReadOnlyList<DurableResolvedExpertInput> ResolvedExperts);
/// <summary><see cref="ProviderProfile"/> is the one profile every llm step runs on:
/// <see cref="DurableMissionPackageValidator.DefaultProviderProfile"/> or an allowed name.</summary>
public sealed record ValidatedDurableMissionPackage(DurableMissionPackageInput Input, MclProgram Ast,
    Dictionary<string, ExpertDefinition> Experts, string ProviderProfile);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DurableMissionPackageInput))]
[JsonSerializable(typeof(DurableResolvedExpertInput))]
internal partial class DurableMissionPackageJsonContext : JsonSerializerContext { }
