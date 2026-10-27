using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ForgeMission.Tests.Cli.Harness;

/// <summary>
/// A local HTTP server on 127.0.0.1 (ephemeral port) that records every request and answers with
/// whatever the test's responder returns. Reached directly by the CLI because NO_PROXY covers it.
/// </summary>
public sealed class HttpStub : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly ConcurrentQueue<StubRequest> requests = new();

    private HttpStub(WebApplication app) => this.app = app;

    /// <summary>Base address, e.g. http://127.0.0.1:54321/.</summary>
    public Uri Address => new(app.Urls.Single().TrimEnd('/') + "/");

    /// <summary>Every request received, in arrival order.</summary>
    public IReadOnlyList<StubRequest> Requests => requests.ToArray();

    public static async Task<HttpStub> StartAsync(Func<StubRequest, StubResponse> respond)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var stub = new HttpStub(builder.Build());
        stub.app.Run(context => stub.ServeAsync(context, respond));
        await stub.app.StartAsync();
        return stub;
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private async Task ServeAsync(HttpContext context, Func<StubRequest, StubResponse> respond)
    {
        var request = await RecordAsync(context.Request);
        requests.Enqueue(request);
        var response = respond(request);
        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        await context.Response.WriteAsync(response.Body);
        if (response.HoldOpen is null) return;
        await context.Response.Body.FlushAsync();
        await response.HoldOpen;
    }

    private static async Task<StubRequest> RecordAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        return new StubRequest(request.Method, request.Path + request.QueryString, headers, await reader.ReadToEndAsync());
    }
}

public sealed record StubRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body);

/// <param name="HoldOpen">When set, the body is sent and the response stays open (a stream that has
/// not ended) until this task completes.</param>
public sealed record StubResponse(int Status, string Body, string ContentType = "application/json", Task? HoldOpen = null);
