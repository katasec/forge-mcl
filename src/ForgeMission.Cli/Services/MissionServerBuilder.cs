using ForgeMission.Core.Manifest;
using ForgeMission.Core.Resolution;
using ForgeMission.Core.Runtime;

namespace ForgeMission.Cli.Services;

// Builds the provider runners a mission run needs from forge.toml profiles, falling back to the
// mission's let-binding context. Used by `forge mcp`; failures are reported through CliDiagnostics
// (Die), so callers just return.
internal static class MissionServerBuilder
{
    // Builds the full runner dictionary from forge.toml profiles.
    // Falls back to let-binding context for the "default" runner if no forge.toml.
    internal static IReadOnlyDictionary<string, IExpertRunner>? BuildRunners(
        ForgeManifest? manifest,
        Dictionary<string, object> seedContext)
    {
        var runners = new Dictionary<string, IExpertRunner>(StringComparer.Ordinal);

        // Build a runner per declared profile.
        if (manifest?.Providers is { Count: > 0 } profiles)
        {
            foreach (var (name, profile) in profiles)
            {
                try { runners[name] = ForgeMission.ChatClients.ChatClients.Build(profile); }
                catch (Exception ex) { CliDiagnostics.Die($"Cannot initialise provider profile '{name}': {ex.Message}"); return null; }
            }
        }

        // If no "default" profile came from forge.toml, fall back to let-binding context.
        if (!runners.ContainsKey("default"))
        {
            var profile = MergeDefaultProfile(manifest, seedContext);

            if (string.IsNullOrWhiteSpace(profile.ApiKey))
            {
                CliDiagnostics.Die("No API key found. Add [providers.default] to forge.toml with apiKey = env(\"MCL_API_KEY\").");
                return null;
            }
            if (string.IsNullOrWhiteSpace(profile.Model))
            {
                CliDiagnostics.Die("No model found. Add [providers.default] to forge.toml with model = env(\"MCL_MODEL\", \"gpt-4o-mini\").");
                return null;
            }

            try { runners["default"] = ForgeMission.ChatClients.ChatClients.Build(profile); }
            catch (Exception ex) { CliDiagnostics.Die($"Cannot initialise default provider: {ex.Message}"); return null; }
        }

        return runners;
    }

    // Layers mission context over the forge.toml [providers.default] block (Core owns the merge).
    // The result may carry an empty ApiKey/Model; the caller decides how a missing value is reported.
    internal static ProviderProfile MergeDefaultProfile(ForgeManifest? manifest, Dictionary<string, object> seedContext)
    {
        var declared = manifest?.Providers?.GetValueOrDefault("default");
        return ProviderProfileResolver.ResolveDefault(declared, key => CliHelpers.GetContextString(seedContext, key));
    }
}
