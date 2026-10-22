using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Qyl;
using Qyl.RealMassTransitDemo;
using MessagingAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Messaging.MessagingAttributes;
using QylAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Qyl.QylAttributes;
using QylTelemetryNames = Qyl.Telemetry.SemanticConventions.Names.QylTelemetryNames;

var uriText = Environment.GetEnvironmentVariable("QYL_RABBITMQ_URI");
if (string.IsNullOrWhiteSpace(uriText))
{
    Console.Error.WriteLine("QYL_RABBITMQ_URI is required.");
    return 2;
}

// The real registration path: AddQyl subscribes to MassTransit's own ActivitySource and installs the
// one native-span processor, so what the exporter receives is what a consumer receives.
var exportedActivities = new List<Activity>();
var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
{
    ApplicationName = "Qyl.RealMassTransitDemo",
    DisableDefaults = true,
});
builder.AddQyl(options =>
{
    options.ServiceName = "qyl-real-masstransit-demo";
    options.EnableLogExport = false;
    options.EnableMetricsExport = false;
    options.EnableSessionTagInheritance = false;
});
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddInMemoryExporter(exportedActivities));
builder.Services.AddMassTransit(configure => configure.UsingRabbitMq((_, rabbit) =>
{
    rabbit.Host(new Uri(uriText));
    rabbit.ConfigureJsonSerializerOptions(options =>
    {
        options.TypeInfoResolverChain.Add(ProbeMessageJsonContext.Default);
        return options;
    });
}));

var host = builder.Build();

// Every broker step shares one explicit deadline, enforced from outside the steps (they run on the
// thread pool, so even a step that blocks synchronously cannot hold the wait) as well as passed into them: a broker that answers TCP but never completes the AMQP handshake, or stalls a publish,
// fails the run with a clear message instead of hanging it. A stuck host is left undisposed, since
// disposing it would wait on the same broker.
var brokerTimeout = TimeSpan.FromSeconds(90);
using var brokerDeadline = new CancellationTokenSource(brokerTimeout, TimeProvider.System);
var bus = host.Services.GetRequiredService<IBusControl>();
Exception? lastConnectFailure = null;
var brokerStep = "host start";
try
{
    await Task.Run(() => RunBrokerStepsAsync(brokerDeadline.Token)).WaitAsync(brokerTimeout, TimeProvider.System);
}
catch (Exception exception) when (exception is TimeoutException || brokerDeadline.IsCancellationRequested)
{
    Console.Error.WriteLine(
        $"RabbitMQ did not complete the MassTransit start, publish, send and stop within {brokerTimeout.TotalSeconds:0}s; stuck at {brokerStep}" +
        (lastConnectFailure is null ? "." : $"; last connect failure: {lastConnectFailure.GetType().Name}: {lastConnectFailure.Message}"));
    return 3;
}

host.Services.GetRequiredService<TracerProvider>().ForceFlush(5_000);
await host.StopAsync();
host.Dispose();

var report = MassTransitReport.Create(
    RuntimeFeature.IsDynamicCodeSupported ? "dynamic-code-supported" : "nativeaot",
    exportedActivities.Select(CapturedActivity.From).ToArray());

var json = JsonSerializer.Serialize(report, RealMassTransitJsonContext.Default.MassTransitReport);
Console.WriteLine(json);

return report.Pass ? 0 : 1;

async Task RunBrokerStepsAsync(CancellationToken deadline)
{
    await host.StartAsync(deadline);
    brokerStep = "bus start";
    await WaitForBusAsync(deadline);

    brokerStep = "publish";
    await bus.Publish(new ProbeEvent("alpha"), deadline);
    Console.WriteLine("published=alpha");

    brokerStep = "send";
    var sendEndpoint = await bus.GetSendEndpoint(new Uri("queue:" + MassTransitReport.SendDestination));
    await sendEndpoint.Send(new ProbeCommand("beta"), deadline);
    Console.WriteLine("sent=beta");

    brokerStep = "bus stop";
    await bus.StopAsync(deadline);
}

// Retries the bus start, five seconds an attempt, until it connects or the deadline cancels, and
// keeps the last failure for the timeout message.
async Task WaitForBusAsync(CancellationToken deadline)
{
    while (true)
    {
        using var attemptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), TimeProvider.System);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline, attemptTimeout.Token);
        try
        {
            await bus.StartAsync(attempt.Token);
            return;
        }
        catch (Exception exception) when (!deadline.IsCancellationRequested &&
                                          exception is RabbitMqConnectionException or OperationCanceledException or TimeoutException)
        {
            lastConnectFailure = exception;
        }

        await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System, deadline);
    }
}

internal sealed record CapturedActivity(
    string TraceId,
    string SpanId,
    string Source,
    string Name,
    string Kind,
    string Status,
    IReadOnlyDictionary<string, string> Tags)
{
    public static CapturedActivity From(Activity activity)
        => new(
            activity.TraceId.ToHexString(),
            activity.SpanId.ToHexString(),
            activity.Source.Name,
            activity.DisplayName,
            activity.Kind.ToString(),
            activity.Status.ToString(),
            activity.TagObjects.ToDictionary(
                static tag => tag.Key,
                static tag => Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty,
                StringComparer.Ordinal));
}

internal sealed record MassTransitReport(
    string RuntimeMode,
    bool Pass,
    string[] Failures,
    CapturedActivity[] Activities)
{
    internal const string SendDestination = "qyl-probe";

    public static MassTransitReport Create(string runtimeMode, CapturedActivity[] activities)
    {
        var failures = new List<string>();
        var massTransitSpans = activities
            .Where(static activity => StringComparer.Ordinal.Equals(
                activity.Source,
                QylTelemetryNames.VendorActivitySources.MassTransit))
            .ToArray();

        // One span per command, not per run: the explicit send names its queue, the publish names
        // the exchange MassTransit derives from the message type. A second span for either means the
        // interceptor was not fully removed.
        var sendSpans = massTransitSpans
            .Where(static span =>
                span.Tags.TryGetValue(MessagingAttributes.DestinationName, out var destination) &&
                StringComparer.Ordinal.Equals(destination, SendDestination))
            .ToArray();
        var publishSpans = massTransitSpans.Except(sendSpans).ToArray();

        if (sendSpans.Length != 1)
            failures.Add($"expected exactly 1 MassTransit span for the '{SendDestination}' send, got {sendSpans.Length.ToString(CultureInfo.InvariantCulture)}");
        if (publishSpans.Length != 1)
            failures.Add($"expected exactly 1 MassTransit span for the publish, got {publishSpans.Length.ToString(CultureInfo.InvariantCulture)}");

        foreach (var span in massTransitSpans)
        {
            // The two attributes the qyl processor owns: without them the collector cannot classify
            // the span, because it classifies on attribute presence and never on the span name.
            RequireTag(
                span,
                QylAttributes.InstrumentationDomain,
                QylAttributes.InstrumentationDomainValues.MessagingMassTransit,
                failures);

            // MassTransit's own attributes: the transport it published through, the destination the
            // span is named after, and the message types it carried. MassTransit still reports the
            // operation through the deprecated messaging.operation key, which is why nothing here
            // asserts a stable messaging.operation.type or .name — it emits neither.
            RequireTag(span, MessagingAttributes.System, MessagingAttributes.SystemValues.Rabbitmq, failures);
            RequirePresentTag(span, MessagingAttributes.DestinationName, failures);
            RequirePresentTag(span, MessagingAttributes.MasstransitMessageTypes, failures);

            if (span.Tags.TryGetValue(MessagingAttributes.DestinationName, out var destination) &&
                !StringComparer.Ordinal.Equals(span.Name, destination + " send"))
                failures.Add($"expected the MassTransit span to be named '{destination} send', got {span.Name}");

            if (!StringComparer.Ordinal.Equals(span.Kind, "Producer"))
                failures.Add($"expected kind Producer, got {span.Kind}");
        }

        return new MassTransitReport(runtimeMode, failures.Count is 0, failures.ToArray(), massTransitSpans);
    }

    private static void RequireTag(CapturedActivity activity, string key, string expected, ICollection<string> failures)
    {
        if (!activity.Tags.TryGetValue(key, out var actual))
        {
            failures.Add($"missing {key}");
            return;
        }

        if (!StringComparer.Ordinal.Equals(actual, expected))
            failures.Add($"expected {key}={expected}, got {actual}");
    }

    private static void RequirePresentTag(CapturedActivity activity, string key, ICollection<string> failures)
    {
        if (!activity.Tags.ContainsKey(key))
            failures.Add($"missing {key}");
    }
}

[JsonSerializable(typeof(MassTransitReport))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class RealMassTransitJsonContext : JsonSerializerContext;
