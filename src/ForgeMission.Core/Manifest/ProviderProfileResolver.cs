namespace ForgeMission.Core.Manifest;

// Resolves the effective "default" provider profile by layering mission context values over the
// forge.toml [providers.default] block. A context value wins; the declared profile fills each gap;
// provider falls back to "openai" and endpoint to empty only when no declared profile supplies them.
//
// This is the one place the fallback is decided; `forge mcp` uses it through
// MissionServerBuilder.MergeDefaultProfile. Callers own validation of the result: the resolved
// ApiKey/Model may be empty, and each caller decides how a missing value is reported.
public static class ProviderProfileResolver
{
    public static ProviderProfile ResolveDefault(ProviderProfile? declared, Func<string, string?> context)
    {
        var apiKey   = context("apiKey")   ?? declared?.ApiKey;
        var model    = context("model")    ?? declared?.Model;
        var provider = context("provider") ?? declared?.Provider ?? "openai";
        var endpoint = context("endpoint") ?? declared?.Endpoint ?? string.Empty;

        return new ProviderProfile
        {
            Provider = provider,
            Model    = model ?? string.Empty,
            ApiKey   = apiKey,
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint
        };
    }
}
