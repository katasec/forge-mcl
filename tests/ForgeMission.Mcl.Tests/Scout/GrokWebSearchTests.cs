using System.Net;
using ForgeMission.Scout;
using ForgeMission.Scout.Grok;

namespace ForgeMission.Tests.Scout;

/// <summary>
/// <see cref="GrokWebSearch"/> reads the whole xAI Responses object and maps the message item's answer
/// text and de-duplicated citation URLs into a grounded result, network-free, in CI.
/// </summary>
public class GrokWebSearchTests
{
    [Fact]
    public async Task Search_ReadsResponseObject_AndMapsGroundedResult()
    {
        const string buffered =
            """{"output":[{"type":"message","content":[{"type":"output_text","text":"42","annotations":[{"type":"url_citation","url":"https://a.example/x"},{"type":"url_citation","url":"https://a.example/x"}]}]}]}""";
        var http = new HttpClient(new JsonHandler(buffered));
        var search = new GrokWebSearch(http, apiKey: "test-key");

        var result = await search.SearchAsync(new WebSearchRequest("q"));

        Assert.Equal("grok", result.Provider);
        Assert.Equal("42", result.Answer);
        Assert.Equal("https://a.example/x", Assert.Single(result.Sources).Url);
    }

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
