using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ForgeMission.Tests.Cli.Harness;

/// <summary>
/// A loopback HTTP proxy that never forwards: it records each request line (absolute-form or
/// CONNECT) and answers 502, so a CONNECT tunnel is refused too. Connections are served one at a
/// time; each must send its request head within a few seconds.
/// </summary>
public sealed class NonForwardingProxy : IAsyncDisposable
{
    private static readonly byte[] BadGateway =
        Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentQueue<ProxyAttempt> attempts = new();
    private readonly Task accepting;

    private NonForwardingProxy(TcpListener listener)
    {
        this.listener = listener;
        Address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        accepting = AcceptAsync();
    }

    public Uri Address { get; }

    /// <summary>Every request that tried to leave through the proxy, in arrival order.</summary>
    public IReadOnlyList<ProxyAttempt> Attempts => attempts.ToArray();

    public static NonForwardingProxy Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new NonForwardingProxy(listener);
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        try { await accepting; }
        catch (OperationCanceledException) { /* stopped while waiting for a connection */ }
        stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            await RefuseAsync(client);
        }
    }

    private async Task RefuseAsync(TcpClient client)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var stream = client.GetStream();
        try
        {
            var head = await ReadRequestHeadAsync(stream, deadline.Token);
            if (head is not null) attempts.Enqueue(head);
            await stream.WriteAsync(BadGateway, deadline.Token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            // The client gave up or never sent a request head; nothing was forwarded either way.
        }
    }

    private static async Task<ProxyAttempt?> ReadRequestHeadAsync(Stream stream, CancellationToken cancellation)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellation);
        if (string.IsNullOrEmpty(requestLine)) return null;
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellation))) { /* drain headers */ }
        var parts = requestLine.Split(' ');
        return parts.Length < 2 ? null : new ProxyAttempt(parts[0], parts[1]);
    }
}

/// <summary>One refused request: the method (GET, CONNECT, ...) and its target as sent to the proxy.</summary>
public sealed record ProxyAttempt(string Method, string Target);
