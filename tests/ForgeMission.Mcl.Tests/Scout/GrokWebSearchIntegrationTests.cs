using ForgeMission.Scout;
using ForgeMission.Scout.Grok;

namespace ForgeMission.Tests.Scout;

/// <summary>
/// Integration test for the Grok-backed <see cref="IWebSearch"/> (Phase 41.1). Hits the real xAI
/// Responses API, so it is skipped unless FORGE_RUN_INTEGRATION=1 and XAI_API_KEY are set.
/// Run with: FORGE_RUN_INTEGRATION=1 XAI_API_KEY=... dotnet test --filter Category=Integration
/// </summary>
public class GrokWebSearchIntegrationTests
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task WebSearch_RealGrok_ReturnsSourceTaggedAnswer()
    {
        var apiKey = IntegrationGate.SkipUnlessOptedIn("XAI_API_KEY");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        IWebSearch search = new GrokWebSearch(http, apiKey);

        var result = await search.SearchAsync(
            new WebSearchRequest("Summarize the latest news from Mario Nawfal's YouTube channel in 3 bullet points."));

        // Grok is an answer engine: expect a synthesized answer plus at least one attributed source.
        Assert.Equal("grok", result.Provider);
        Assert.False(string.IsNullOrWhiteSpace(result.Answer));
        Assert.NotEmpty(result.Sources);
        Assert.All(result.Sources, s =>
        {
            Assert.Equal("grok", s.Provider);           // source attribution travels with each hit
            Assert.False(string.IsNullOrWhiteSpace(s.Url));
        });
    }
}
