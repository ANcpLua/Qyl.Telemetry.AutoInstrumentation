using System.Net;
using System.Net.Sockets;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class CollectorDiscoveryTests
{
    [TestMethod]
    public void TcpProbe_ListeningLoopbackPort_ReturnsTrue()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.IsTrue(CollectorDiscovery.TcpProbe("localhost", port));
    }

    [TestMethod]
    public void TcpProbe_ClosedLoopbackPort_ReturnsFalse()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        Assert.IsFalse(CollectorDiscovery.TcpProbe("localhost", port));
    }
}
