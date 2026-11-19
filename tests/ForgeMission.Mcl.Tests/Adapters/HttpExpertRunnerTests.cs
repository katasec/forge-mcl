using System.Net;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;

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

    [Theory]
    [InlineData("""{"text":"looks fine"}""")]
    [InlineData("""{"text":"unsure","status":"maybe"}""")]
    [InlineData("not json")]
    public async Task RunAsync_JudgeReplyWithoutVerdict_FailsVerification(string body)
    {
        var result = await RunJudge(body);

        Assert.Equal("fail", result.Status);
        Assert.StartsWith("The judge reply was not a valid verdict:", result.Reason);
        Assert.Equal(body, result.Text);
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("fail")]
    public async Task RunAsync_JudgeVerdict_ReturnsItsStatus(string status)
    {
        var result = await RunJudge($$"""{"text":"judged","status":"{{status}}","reason":"because"}""");

        Assert.Equal(status,   result.Status);
        Assert.Equal("judged", result.Text);
        Assert.Equal("because", result.Reason);
    }

    [Fact]
    public async Task RunAsync_NonJudgeReplyWithoutStatus_Passes()
    {
        var runner = new HttpExpertRunner(new HttpClient(new MockHttpHandler("""{"text":"done"}""")));
        var expert = new ExpertDefinition("Worker", "In", "Out", "", Kind: "http", Endpoint: "http://test.local/w");

        var result = await runner.RunAsync(expert, new Dictionary<string, object>());

        Assert.Equal("pass", result.Status);
        Assert.Equal("done", result.Text);
    }

    private static Task<StepEnvelope> RunJudge(string body)
    {
        var runner = new HttpExpertRunner(new HttpClient(new MockHttpHandler(body)));
        var expert = new ExpertDefinition("Judge", "In", "Verdict", "", Role: "judge", Kind: "http", Endpoint: "http://test.local/judge");
        return runner.RunAsync(expert, new Dictionary<string, object>());
    }

    [Fact]
    public async Task StreamAsync_YieldsEnvelopeText()
    {
        var handler = new MockHttpHandler("""{"text":"streamed result","status":"pass"}""");
        IExpertRunner runner = new HttpExpertRunner(new HttpClient(handler));
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
