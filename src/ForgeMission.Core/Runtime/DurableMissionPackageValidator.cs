using System.Security.Cryptography;
using System.Text;
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
    // The complete package travels in the one Host start command/checkpoint. These deliberately
    // small content limits, plus Host's exact serialized-command guard, make that invariant true
    // rather than claiming an image-sized package can fit in a 32 KiB durable command.
    public const int MaxPackageUtf8Bytes = 8 * 1024;
    private const int MaxMissionChars = 4 * 1024;
    private const int MaxExpertChars = 8 * 1024;
    private const int MaxExperts = 2;

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
            package.MissionSource.Length > MaxMissionChars || string.IsNullOrWhiteSpace(package.RootMissionName) ||
            string.IsNullOrWhiteSpace(package.RootInputName) || package.ResolvedExperts.Count is 0 or > MaxExperts ||
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
                    string.IsNullOrWhiteSpace(entry.LockPath) || entry.ExpertMarkdown.Length > MaxExpertChars ||
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
