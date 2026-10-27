using System.Net;
using System.Net.Sockets;

namespace ForgeMission.Cli.Services;

// Picks a free loopback TCP port by binding one briefly, then releasing it. The listener is
// disposed before the port is returned, so the caller is free to bind it.
internal static class FreePort
{
    public static int Find()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
