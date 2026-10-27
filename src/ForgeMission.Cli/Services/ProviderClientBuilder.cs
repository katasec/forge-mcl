using Scout;
using Scout.Grok;
using ForgeMission.Cli;

// Live-retrieval wiring stays in CLI; chat-provider construction lives in ForgeMission.ChatClients.
public static class ProviderClientBuilder
{
    // Live-retrieval backend for kind:search experts (Phase 41). Implicitly Grok for the POC.
    // Returns null when no xAI key is present — missions without kind:search are unaffected; a
    // kind:search step then fails with a clear "IWebSearch not configured" error.
    internal static IWebSearch? BuildWebSearch(IHttpClientFactory clients)
    {
        var key = Environment.GetEnvironmentVariable("XAI_API_KEY")
                  ?? Environment.GetEnvironmentVariable("GROK_API_KEY");
        return string.IsNullOrWhiteSpace(key) ? null : new GrokWebSearch(clients.CreateClient(CliHttpClients.WebSearch), key);
    }
}
