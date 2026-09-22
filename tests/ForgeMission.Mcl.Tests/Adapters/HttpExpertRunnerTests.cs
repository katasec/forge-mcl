using System.Net;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;

namespace ForgeMission.Tests.Adapters;

public class HttpExpertRunnerTests
{
    [Fact]
    public async Task RunAsync_PostsContextAsJsonAndReturnsEnvelope()
    {
        var handler = new MockHttpHandler("""{"text":"scored","status":"pass"}""");
        var runner  = new HttpExpertRunner(new HttpClient(handler));
        var expert  = new ExpertDefinition("Scorer", "Input", "Score", "", Kind: "http", Endpoint: "http://test.local/score");
        var context = new Dictionary<string, object> { ["proposal"] = "some input" };

        var result = await runner.RunAsync(expert, context);

        Assert.Equal("scored",             result.Text);
        Assert.Equal("pass",               result.Status);
        Assert.Equal("http://test.local/score", handler.LastRequestUri);
        Assert.Contains("\"proposal\"",    handler.LastRequestBody);
        Assert.Contains("\"some input\"",  handler.LastRequestBody);
    }

    [Fact]
    public async Task RunAsync_FailEnvelope_ReturnsFailStatus()
    {
        var handler = new MockHttpHandler("""{"text":"too long","status":"fail","reason":"exceeds limit"}""");
        var runner  = new HttpExpertRunner(new HttpClient(handler));
        var expert  = new ExpertDefinition("WordCheck", "Input", "Result", "", Kind: "http", Endpoint: "http://test.local/check");

        var result = await runner.RunAsync(expert, new Dictionary<string, object>());

        Assert.Equal("fail",          result.Status);
        Assert.Equal("exceeds limit", result.Reason);
    }

    [Fact]
    public async Task StreamAsync_YieldsEnvelopeText()
    {
        var handler = new MockHttpHandler("""{"text":"streamed result","status":"pass"}""");
        var runner  = new HttpExpertRunner(new HttpClient(handler));
        var expert  = new ExpertDefinition("E", "In", "Out", "", Kind: "http", Endpoint: "http://test.local/e");

        var chunks = new List<string>();
        await foreach (var chunk in runner.StreamAsync(expert, new Dictionary<string, object>()))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal("streamed result", chunks[0]);
    }
}

internal sealed class MockHttpHandler(string response) : HttpMessageHandler
{
    public string LastRequestUri  { get; private set; } = "";
    public string LastRequestBody { get; private set; } = "";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri  = request.RequestUri?.ToString() ?? "";
        LastRequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
