using System.Net;
using System.Net.Sockets;
using Katasec.OaiServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ForgeMission.Tests.Integration;

internal sealed class OaiServerFixture : IAsyncDisposable
{
    private readonly WebApplication _app;

    public int Port { get; }
    public string BaseUrl { get; }

    private OaiServerFixture(WebApplication app, int port)
    {
        _app    = app;
        Port    = port;
        BaseUrl = $"http://localhost:{port}";
    }

    public static async Task<OaiServerFixture> StartAsync(
        IChatClient chatClient,
        string agentId = "test-agent",
        ISessionStore? sessionStore = null)
    {
        var port  = FindFreePort();
        var store = sessionStore ?? new InMemorySessionStore();

        var server  = new OaiServer(chatClient, store, agentId);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseSetting("urls", $"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        server.Map(app);

        await app.StartAsync();
        return new OaiServerFixture(app, port);
    }

    public async ValueTask DisposeAsync() => await _app.StopAsync();

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
