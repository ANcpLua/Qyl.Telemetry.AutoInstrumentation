using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Qyl.Telemetry.Testing;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NativeOtlpTransportTests(TestContext testContext)
{
    private static readonly Action<Microsoft.Extensions.Logging.ILogger, long, Exception?> Completed = LoggerMessage.Define<long>(
        LogLevel.Information, new EventId(19, "WorkCompleted"), "Completed {Sequence}");

    [TestMethod]
    [DataRow(OtlpTransport.HttpProtobuf)]
    [DataRow(OtlpTransport.Grpc)]
    public async Task Real_host_exports_all_signals_on_dynamic_ports_using_the_selected_protocol(OtlpTransport transport)
    {
        var sourceName = "Shop.Checkout." + Guid.NewGuid().ToString("N");
        var result = await QylTest.TraceAsync(async capture =>
        {
            var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.AddQyl(options =>
            {
                options.ServiceName = "checkout-transport";
                options.ServiceVersion = "1.0.0";
                options.CollectorEndpoint = transport == OtlpTransport.Grpc ? capture.GrpcEndpoint : capture.Endpoint;
                options.CollectorProtocol = transport == OtlpTransport.Grpc ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf;
                options.EnableCollectorDiscovery = false;
                options.AdditionalSources.Add(sourceName);
                options.AdditionalMeters.Add(sourceName);
            });
            builder.Services.AddOpenTelemetry().ConfigureResource(resource => resource.AddAttributes(
                [new KeyValuePair<string, object>(capture.Partition.ResourceAttribute, capture.Partition.Value)]));
            using var host = builder.Build();
            await host.StartAsync(testContext.CancellationToken);
            using var source = new ActivitySource(sourceName, "1.0.0");
            using var meter = new Meter(sourceName, "1.0.0");
            using (var activity = source.CreateActivity("checkout.operation", ActivityKind.Internal)?.Start())
            {
                Assert.IsNotNull(activity);
                activity.SetTag("checkout.sequence", 9_007_199_254_740_993L).SetTag("checkout.accepted", false);
                Completed(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(sourceName), 9_007_199_254_740_993L, null);
                meter.CreateCounter<long>("checkout.orders", "{order}").Add(7);
            }
            Assert.IsTrue(host.Services.GetRequiredService<TracerProvider>().ForceFlush());
            Assert.IsTrue(host.Services.GetRequiredService<MeterProvider>().ForceFlush());
            // Host disposal owns the logger provider's shutdown export. Do not manually
            // flush logs: this also verifies first-batch delivery over both transports.
            await host.StopAsync(testContext.CancellationToken);
        }, trace => trace.Spans.Any(static span => span.Name == "checkout.operation")
                    && trace.Logs.Any(static log => log.EventName == "WorkCompleted")
                    && trace.Metrics.Any(static metric => metric.Metric.Name == "checkout.orders"),
            cancellationToken: testContext.CancellationToken);

        var span = Assert.ContainsSingle(result.Spans.Where(static span => span.Name == "checkout.operation"));
        Assert.IsNotNull(span.Scope);
        Assert.AreEqual(sourceName, span.Scope.Name);
        Assert.AreEqual(9_007_199_254_740_993L, span.GetInt64("checkout.sequence"));
        Assert.IsFalse(span.GetBoolean("checkout.accepted"));
        var log = Assert.ContainsSingle(result.Logs.Where(static log => log.EventName == "WorkCompleted"));
        Assert.AreEqual(span.TraceId, log.TraceId);
        Assert.AreEqual(span.SpanId, log.SpanId);
        Assert.AreEqual(9_007_199_254_740_993L, log.Attributes["Sequence"]);
        var counters = result.Metrics.Where(static metric => metric.Metric.Name == "checkout.orders").ToArray();
        Assert.IsNotEmpty(counters);
        foreach (var counter in counters)
        {
            Assert.AreEqual(MetricCorrelation.ResourcePartition, counter.Correlation);
            Assert.AreEqual(7L, Assert.ContainsSingle(Assert.IsInstanceOfType<SumData>(counter.Metric.Data).Points).Value);
        }
        Assert.IsTrue(result.Capture.Requests.All(request => request.Transport == transport));
        Assert.AreSequenceEqual(["logs", "metrics", "traces"], result.Capture.Requests.Select(static request => request.Signal).Distinct().Order(StringComparer.Ordinal));
        Assert.IsTrue(result.Capture.Receipts.All(static receipt => receipt is { Rejected: 0, Error: null }));
    }

    [TestMethod]
    public void Invalid_protocol_is_rejected_before_building_exporters()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var services = builder.Services.ToArray();
        var failure = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => builder.AddQyl(
            options => options.CollectorProtocol = (OtlpExportProtocol)byte.MaxValue));
        Assert.AreEqual(nameof(QylTelemetryOptions.CollectorProtocol), failure.ParamName);
        Assert.AreSequenceEqual(services, builder.Services);
    }
}
