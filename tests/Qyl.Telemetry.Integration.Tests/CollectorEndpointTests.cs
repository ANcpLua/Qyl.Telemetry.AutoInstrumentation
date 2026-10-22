using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class CollectorEndpointTests(TestContext testContext)
{
    [TestMethod]
    public async Task HttpCollectorEndpoint_WithBasePath_KeepsBasePath()
    {
        var port = FreePort();
        using var collector = new HttpListener();
        collector.Prefixes.Add($"http://127.0.0.1:{port}/");
        collector.Start();
        var received = collector.GetContextAsync();

        var sourceName = "Shop.Endpoint." + Guid.NewGuid().ToString("N");
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.AddQyl(options =>
        {
            options.CollectorEndpoint = new Uri($"http://127.0.0.1:{port}/otlp");
            options.EnableCollectorDiscovery = false;
            options.EnableLogExport = false;
            options.EnableMetricsExport = false;
            options.AdditionalSources.Add(sourceName);
        });
        using var host = builder.Build();
        await host.StartAsync(testContext.CancellationToken);
        using (var source = new ActivitySource(sourceName))
        using (source.StartActivity("checkout.operation"))
        {
        }

        Assert.IsTrue(host.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000));
        var request = await received.WaitAsync(TimeSpan.FromSeconds(10), testContext.CancellationToken);
        request.Response.Close();
        await host.StopAsync(testContext.CancellationToken);

        Assert.AreEqual("/otlp/v1/traces", request.Request.Url!.AbsolutePath);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
