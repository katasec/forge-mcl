using ForgeMission.Scout;
using ForgeMission.Scout.Grok;
using Microsoft.Extensions.Options;

namespace ForgeMission.Cli.Services;

// Builds the live-retrieval backend for kind:search experts (always Grok). Chat-provider
// construction lives in ForgeMission.ChatClients.
internal static class WebSearchBuilder
{
    // Returns null when no xAI key is present — missions without kind:search are unaffected; a
    // kind:search step then fails with "kind: search requires a configured IWebSearch (Scout)".
    internal static IWebSearch? Build(IOptions<WebSearchOptions> options, IHttpClientFactory clients)
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
