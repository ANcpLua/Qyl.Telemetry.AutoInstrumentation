using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Qyl.Telemetry.Tests;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NativeOtlpTransportTests(TestContext testContext)
{
    private const string OperationName = "checkout.operation";
    private const string LogEventName = "WorkCompleted";
    private const string CounterName = "checkout.orders";
    private const string SequenceAttribute = "checkout.sequence";
    private const string AcceptedAttribute = "checkout.accepted";
    private const long SequenceBeyondDoublePrecision = 9_007_199_254_740_993L;

    private static readonly Action<Microsoft.Extensions.Logging.ILogger, long, Exception?> Completed = LoggerMessage.Define<long>(
        LogLevel.Information, new EventId(19, LogEventName), "Completed {Sequence}");

    [TestMethod]
    [DataRow(OtlpTransport.HttpProtobuf)]
    [DataRow(OtlpTransport.Grpc)]
    public async Task AddQylExport_SelectedProtocol_KeepsSpanSourceScope(OtlpTransport transport)
    {
        var export = await ExportAsync(transport);

        var span = Assert.ContainsSingle(export.Evidence.Spans.Where(static span => span.Name == OperationName));
        Assert.IsNotNull(span.Scope);
        Assert.AreEqual(export.SourceName, span.Scope.Name);
    }

    [TestMethod]
    [DataRow(OtlpTransport.HttpProtobuf)]
    [DataRow(OtlpTransport.Grpc)]
    public async Task AddQylExport_SelectedProtocol_KeepsInt64BeyondDoublePrecisionOnSpan(OtlpTransport transport)
    {
        var export = await ExportAsync(transport);

        var span = Assert.ContainsSingle(export.Evidence.Spans.Where(static span => span.Name == OperationName));
        Assert.AreEqual(SequenceBeyondDoublePrecision, span.GetInt64(SequenceAttribute));
    }

    [TestMethod]
    [DataRow(OtlpTransport.HttpProtobuf)]
    [DataRow(OtlpTransport.Grpc)]
    public async Task AddQylExport_SelectedProtocol_KeepsFalseBooleanOnSpan(OtlpTransport transport)
    {
        var export = await ExportAsync(transport);

        var span = Assert.ContainsSingle(export.Evidence.Spans.Where(static span => span.Name == OperationName));
        Assert.IsFalse(span.GetBoolean(AcceptedAttribute));
    }

    [TestMethod]
    [DataRow(OtlpTransport.HttpProtobuf)]
    [DataRow(OtlpTransport.Grpc)]
    public async Task AddQylExport_SelectedProtocol_CorrelatesLogWithSpan(OtlpTransport transport)
    {
        var export = await ExportAsync(transport);

        var span = Assert.ContainsSingle(export.Evidence.Spans.Where(static span => span.Name == OperationName));
        var log = Assert.ContainsSingle(export.Evidence.Logs.Where(static log => log.EventName == LogEventName));
        Assert.AreEqual(span.TraceId, log.TraceId);
        Assert.AreEqual(span.SpanId, log.SpanId);
    }

    [TestMethod]
    [DataRow(OtlpTransport.HttpProtobuf)]
    [DataRow(OtlpTransport.Grpc)]
    public async Task AddQylExport_SelectedProtocol_KeepsInt64BeyondDoublePrecisionOnLog(OtlpTransport transport)
    {
        var export = await ExportAsync(transport);

        var log = Assert.ContainsSingle(export.Evidence.Logs.Where(static log => log.EventName == LogEventName));
        Assert.AreEqual(SequenceBeyondDoublePrecision, log.Attributes["Sequence"]);
    }

    [TestMethod]
    [DataRow(OtlpTransport.HttpProtobuf)]
    [DataRow(OtlpTransport.Grpc)]
    public async Task AddQylExport_SelectedProtocol_CorrelatesCounterThroughResourcePartition(OtlpTransport transport)
    {
        var export = await ExportAsync(transport);

        var counters = export.Evidence.Metrics.Where(static metric => metric.Metric.Name == CounterName).ToArray();
        Assert.IsNotEmpty(counters);
        foreach (var counter in counters)
            Assert.AreEqual(MetricCorrelation.ResourcePartition, counter.Correlation);
    }

    [TestMethod]
    [DataRow(OtlpTransport.HttpProtobuf)]
    [DataRow(OtlpTransport.Grpc)]
    public async Task AddQylExport_SelectedProtocol_ExportsCounterSingleSumPoint(OtlpTransport transport)
    {
        var export = await ExportAsync(transport);

        var counters = export.Evidence.Metrics.Where(static metric => metric.Metric.Name == CounterName).ToArray();
        Assert.IsNotEmpty(counters);
        foreach (var counter in counters)
            Assert.AreEqual(7L, Assert.ContainsSingle(Assert.IsInstanceOfType<SumData>(counter.Metric.Data).Points).Value);
    }

    [TestMethod]
    [DataRow(OtlpTransport.HttpProtobuf)]
    [DataRow(OtlpTransport.Grpc)]
    public async Task AddQylExport_SelectedProtocol_SendsEveryRequestOverThatProtocol(OtlpTransport transport)
    {
        var export = await ExportAsync(transport);

        Assert.IsTrue(export.Evidence.Capture.Requests.All(request => request.Transport == transport));
    }

    [TestMethod]
    [DataRow(OtlpTransport.HttpProtobuf)]
    [DataRow(OtlpTransport.Grpc)]
    public async Task AddQylExport_SelectedProtocol_ExportsLogsMetricsAndTraces(OtlpTransport transport)
    {
        var export = await ExportAsync(transport);

        Assert.AreSequenceEqual(["logs", "metrics", "traces"],
            export.Evidence.Capture.Requests.Select(static request => request.Signal).Distinct().Order(StringComparer.Ordinal));
    }

    [TestMethod]
    [DataRow(OtlpTransport.HttpProtobuf)]
    [DataRow(OtlpTransport.Grpc)]
    public async Task AddQylExport_SelectedProtocol_ReceiverAcceptsEveryExport(OtlpTransport transport)
    {
        var export = await ExportAsync(transport);

        Assert.IsTrue(export.Evidence.Capture.Receipts.All(static receipt => receipt is { Rejected: 0, Error: null }));
    }

    [TestMethod]
    public void AddQyl_UndefinedProtocol_ThrowsForCollectorProtocol()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });

        var failure = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => builder.AddQyl(
            static options => options.CollectorProtocol = (OtlpExportProtocol)byte.MaxValue));
        Assert.AreEqual(nameof(QylTelemetryOptions.CollectorProtocol), failure.ParamName);
    }

    [TestMethod]
    public void AddQyl_UndefinedProtocol_RegistersNoServices()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var servicesBefore = builder.Services.ToArray();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => builder.AddQyl(
            static options => options.CollectorProtocol = (OtlpExportProtocol)byte.MaxValue));
        Assert.AreSequenceEqual(servicesBefore, builder.Services);
    }

    /// <summary>
    /// Runs a real host whose AddQyl exporters use <paramref name="transport" /> against an owned receiver, and
    /// returns once the span, the log and the counter have all arrived.
    /// </summary>
    private async Task<TransportExport> ExportAsync(OtlpTransport transport)
    {
        var sourceName = "Shop.Checkout." + Guid.NewGuid().ToString("N");
        var evidence = await TelemetryCapture.TraceAsync(async capture =>
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
            using (var activity = source.CreateActivity(OperationName, ActivityKind.Internal)?.Start()
                                  ?? throw new InvalidOperationException($"AddQyl did not subscribe to {sourceName}."))
            {
                activity.SetTag(SequenceAttribute, SequenceBeyondDoublePrecision).SetTag(AcceptedAttribute, false);
                Completed(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(sourceName), SequenceBeyondDoublePrecision, null);
                meter.CreateCounter<long>(CounterName, "{order}").Add(7);
            }
            if (!host.Services.GetRequiredService<TracerProvider>().ForceFlush())
                throw new InvalidOperationException("The tracer provider did not flush.");
            if (!host.Services.GetRequiredService<MeterProvider>().ForceFlush())
                throw new InvalidOperationException("The meter provider did not flush.");
            // Host disposal owns the logger provider's shutdown export. Do not manually
            // flush logs: this also verifies first-batch delivery over both transports.
            await host.StopAsync(testContext.CancellationToken);
        }, static trace => trace.Spans.Any(static span => span.Name == OperationName)
                           && trace.Logs.Any(static log => log.EventName == LogEventName)
                           && trace.Metrics.Any(static metric => metric.Metric.Name == CounterName),
            cancellationToken: testContext.CancellationToken);
        return new TransportExport(sourceName, evidence);
    }

    private sealed record TransportExport(string SourceName, TraceEvidence Evidence);
}
