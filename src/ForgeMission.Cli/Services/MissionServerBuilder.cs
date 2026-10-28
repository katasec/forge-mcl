using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Manifest;
using ForgeMission.Core.Resolution;
using ForgeMission.Core.Runtime;
using ForgeMission.Serve;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeMission.Cli.Services;

// Loads the mission behind an AgentConfig and builds the wire server, plus the provider-runner
// construction it depends on. Shared by `forge serve`, `forge claude`, `forge connect` and
// `forge mcp`; each reports its own failure through CliDiagnostics (Die), so callers just return.
internal static class MissionServerBuilder
{
    // Loads the mission behind an AgentConfig and builds the wire server, ready to start.
    // Shared by `forge serve` (blocking RunAsync) and `forge claude` (background StartAsync).
    // Returns null after reporting the error (Die) — callers just return.
    internal static async Task<(WebApplication App, string MissionPath, bool AnthropicDoor, bool OpenAiDoor)?> TryBuildMissionServerAsync(
        IOptions<WebSearchOptions> webSearchOptions, IHttpClientFactory clients, ILogger logger, AgentConfig config, string agentDir, CancellationToken cancellationToken)
    {
        var missionPath = Path.GetFullPath(Path.Combine(agentDir, config.Mission));
        var lockPath    = CliHelpers.MclLockPath(Path.GetDirectoryName(missionPath)!);

        if (!File.Exists(missionPath)) { CliDiagnostics.Die($"Mission file not found: {missionPath}"); return null; }
        if (!File.Exists(lockPath))    { CliDiagnostics.Die("MCL007 Mission not initialised — run 'forge init' first."); return null; }

        var source = await CliHelpers.TryReadFile(missionPath, cancellationToken);
        if (source is null) return null;

        var ast = CliHelpers.TryParse(source, missionPath);
        if (ast is null) return null;

        LockFile lockFile;
        try { lockFile = LockFileIO.Read(lockPath); }
        catch (Exception ex) { CliDiagnostics.Die($"Cannot read mcl.lock: {ex.Message}"); return null; }

        Dictionary<string, ExpertDefinition> expertDefs;
        try { expertDefs = ExpertLoader.LoadFromLockFile(lockFile, Path.GetDirectoryName(missionPath)!); }
        catch (AggregateExpertLoadException ex) { foreach (var e in ex.Errors) CliDiagnostics.ReportExpertDiagnostic(e); throw new ForgeExitException(1); }
        catch (ExpertLoadException ex)           { CliDiagnostics.ReportExpertDiagnostic(ex); throw new ForgeExitException(1); }

        if (!CliHelpers.TryValidate(ast, expertDefs, missionFilePath: missionPath)) return null;

        Dictionary<string, object> seedContext;
        try { seedContext = ContextBuilder.Seed(ast, new Dictionary<string, string>()); }
        catch (InvalidOperationException ex) { CliDiagnostics.Die(ex.Message); return null; }

        ForgeManifest? manifest = null;
        try { manifest = ForgeTomlReader.TryRead(missionPath); }
        catch (ForgeTomlException ex) { CliDiagnostics.Die(ex.Message); return null; }

        var runners = BuildRunners(manifest, seedContext);
        if (runners is null) return null;

        var defaultRunner = runners.TryGetValue("default", out var dr)
            ? dr
            : runners.Values.First();

        // Wire doors (42.4): ONE app serves every enabled wire. `wire:` in agent.yaml is a disable
        // switch, owned by ForgeServe.WirePolicy; each enabled door gets its own MissionChatClient over
        // the same mission core, built by Core's MissionServerFactory — the Anthropic wire hands the
        // mission the FULL conversation (context["conversation"]/["system"], goal = last text block of
        // the last user message); the OpenAI wire keeps the legacy last-turn behaviour so existing users
        // don't regress (42.1 decision, unchanged).
        if (!WirePolicy.TryParse(config.Wire, out var anthropicEnabled, out var openAiEnabled, out var wireError))
        {
            CliDiagnostics.Die(wireError!);
            return null;
        }

        // kind:search backend (41.2) — same seam as `forge run` and the cloud runner; null when
        // XAI_API_KEY is unset, so missions without kind:search are unaffected.
        var webSearch = ProviderClientBuilder.BuildWebSearch(webSearchOptions, clients);

        var (anthropicDoor, openAiDoor) = MissionServerFactory.BuildDoors(
            ast, expertDefs, defaultRunner, anthropicEnabled, openAiEnabled, webSearch);

        // Aux passthrough (42.3 §0): client housekeeping (title-gen, state-check) is answered by a
        // plain provider model; the mission never runs — and never bills — for those. The aux client is
        // OPTIONAL: if its provider can't be built (missing key, bad profile), serving deliberately
        // degrades to a null aux client and the server answers housekeeping with its canned replies;
        // the failure is logged as a Warning.
        IChatClient? auxClient = null;
        if (anthropicDoor is not null && ResolveDefaultProfile(manifest, seedContext) is { } auxProfile)
        {
            try { auxClient = ForgeMission.ChatClients.ChatClients.BuildChatClient(auxProfile); }
            catch (Exception ex) { logger.AuxClientUnavailable(ex, auxProfile.Provider); auxClient = null; }
        }

        var app = ForgeServe.BuildApp(config.Id, config.Port, anthropicDoor, openAiDoor, auxClient);

        return (app, missionPath, anthropicDoor is not null, openAiDoor is not null);
    }

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

    // The default provider profile, resolved the same way BuildRunners falls back:
    // forge.toml [providers.default] first, then the mission's let-binding context.
    // Returns null when either apiKey or model is missing.
    internal static ProviderProfile? ResolveDefaultProfile(ForgeManifest? manifest, Dictionary<string, object> seedContext)
    {
        var profile = MergeDefaultProfile(manifest, seedContext);
        if (string.IsNullOrWhiteSpace(profile.ApiKey) || string.IsNullOrWhiteSpace(profile.Model)) return null;
        return profile;
    }

    // Layers mission context over the forge.toml [providers.default] block (Core owns the merge).
    // The result may carry an empty ApiKey/Model; the caller decides how a missing value is reported.
    internal static ProviderProfile MergeDefaultProfile(ForgeManifest? manifest, Dictionary<string, object> seedContext)
    {
        var declared = manifest?.Providers?.GetValueOrDefault("default");
        return ProviderProfileResolver.ResolveDefault(declared, key => CliHelpers.GetContextString(seedContext, key));
    }
}
