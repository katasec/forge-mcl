using Scout;
using Scout.Grok;
using Microsoft.Extensions.Options;

namespace ForgeMission.Cli.Services;

// Live-retrieval wiring stays in CLI; chat-provider construction lives in ForgeMission.ChatClients.
public static class ProviderClientBuilder
{
    // Live-retrieval backend for kind:search experts (Phase 41). Implicitly Grok for the POC.
    // Returns null when no xAI key is present — missions without kind:search are unaffected; a
    // kind:search step then fails with a clear "IWebSearch not configured" error.
    internal static IWebSearch? BuildWebSearch(IOptions<WebSearchOptions> options, IHttpClientFactory clients)
    {
        var key = options.Value.ApiKey;
        return string.IsNullOrWhiteSpace(key) ? null : new GrokWebSearch(clients.CreateClient(CliHttpClients.WebSearch), key);
    }
}

// The live-retrieval backend's key, configured by the CLI host from XAI_API_KEY, else GROK_API_KEY.
internal sealed class WebSearchOptions
{
    public string? ApiKey { get; set; }
}
