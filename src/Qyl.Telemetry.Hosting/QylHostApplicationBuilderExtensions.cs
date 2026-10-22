using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Qyl.Telemetry.AutoInstrumentation;
using Qyl.Telemetry.AutoInstrumentation.GeneratedCode;
using Qyl.Telemetry.AutoInstrumentation.Hosting;
using DeploymentAttributes = Qyl.Telemetry.SemanticConventions.Attributes.Deployment.DeploymentAttributes;

namespace Qyl;

/// <summary>
/// The one-line qyl onboarding surface: <c>builder.AddQyl()</c> activates the qyl
/// auto-instrumentation bootstrap, wires the OpenTelemetry SDK with ASP.NET Core's own server span
/// (enriched by the qyl middleware, never duplicated), every row of the native-source table — the
/// BCL's outbound <c>System.Net.Http</c> span among them — plus the native and
/// qyl-owned meter inventory and the application scope behind <c>Qyl.Spans.Source</c>; derives the
/// resource from the build and the host; stamps a request's <c>baggage</c> member <c>session.id</c>
/// onto its server span and carries <c>session.id</c> from a span to its in-process descendants; and
/// exports traces, metrics, and logs over OTLP — to
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> when set, otherwise to a locally discovered qyl collector.
/// qyl writes no baggage, so the session reaches the next process only in baggage the application
/// puts there. The specialist EF Core and SqlClient packages emit spans under the single qyl
/// ActivitySource, so they need no extra source registration here. Applications still install the
/// specialist package for each dependency-heavy database integration; adding competing native
/// sources would double-report the same operations.
/// <para>
/// The method is idempotent: a second call — a composing library and the application both asking
/// for it — returns the builder untouched, so the pipeline is built once. The first call's options
/// win.
/// </para>
/// </summary>
public static class QylHostApplicationBuilderExtensions
{
    private const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    // Hosts that run the application's startup path to inspect it rather than to serve traffic.
    // GetDocument.Insider is the build-time OpenAPI document generator: it loads the application,
    // builds its host, reads the document and exits — inside the developer's or CI's environment,
    // OTEL_EXPORTER_OTLP_ENDPOINT included. Without this gate every build ships build-time spans and
    // log records to the real collector. HostApplicationBuilder takes ApplicationName from the entry
    // assembly, and under this host the entry assembly is the tool, so the name identifies it
    // without reflection.
    //
    // Not covered, deliberately: `dotnet ef` and WebApplicationFactory run the application as its
    // own entry assembly, so nothing in the environment distinguishes them from the real host. A
    // design-time host that must not export sets QylTelemetryOptions.RequireConfiguredEndpoint and leaves
    // the endpoint unset.
    private static readonly string[] BuildTimeHostApplicationNames = ["GetDocument.Insider"];

    // Four 100 ms connect attempts plus a DNS lookup; the probe runs while the host wires itself, so
    // this bound is only reached when the SDK builds its pipeline immediately after AddQyl.
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMilliseconds(600);

    /// <summary>Activate qyl instrumentation, session-tag inheritance, and OTLP export.</summary>
    /// <remarks>
    /// Wrapper-based GenAI telemetry additionally requires opting the library itself in:
    /// <c>agent.AsBuilder().UseOpenTelemetry().Build()</c> for Microsoft.Agents.AI, or
    /// <c>chatClient.AsBuilder().UseOpenTelemetry().Build()</c> for a bare IChatClient. The
    /// Workflows use <c>WorkflowBuilder.WithOpenTelemetry()</c>. MCP and CoreWCF emit from
    /// their official SDK paths without a separate telemetry wrapper.
    /// </remarks>
    [SuppressMessage("OpenTelemetry", "QYL0700",
        Justification = "The endpoint is the caller's, the environment's or discovery's, applied in ConfigureExporter; with none of them the exporter's localhost default is the local qyl collector, which is the intended default.")]
    public static IHostApplicationBuilder AddQyl(
        this IHostApplicationBuilder builder,
        Action<QylTelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Idempotent, like the instrumentation registration nested below. Each call queues its own
        // WithTracing callback against the one TracerProvider, so a second one would add a second
        // session processor, a second native processor and a second OTLP exporter — every span,
        // metric and log exported twice, silently. That is not a hypothetical: an application that
        // follows the README's builder.AddQyl() and also calls AddQylApi, which calls AddQyl itself,
        // does exactly this.
        //
        // A repeated call without options is that composition and is simply ignored. A repeated call
        // WITH options is a different thing: 15.0.0 discarded those options silently, so an
        // application configuring a service name or an endpoint on its second call ran with neither
        // and had no way to notice. Since the registration order is the application's own, only the
        // application can resolve it, and it needs to be told.
        if (builder.Services.Any(static service => service.ServiceType == typeof(QylRegistration)))
        {
            if (configure is not null)
            {
                throw new InvalidOperationException(
                    "AddQyl was already called on this builder, so these options would be discarded. " +
                    "Configure qyl once — AddQylApi calls AddQyl itself, so pass the options there, " +
                    "or call AddQyl with options before it.");
            }

            return builder;
        }

        var options = new QylTelemetryOptions();
        configure?.Invoke(options);
        if (options.CollectorProtocol is not (OtlpExportProtocol.Grpc or OtlpExportProtocol.HttpProtobuf))
            throw new ArgumentOutOfRangeException(nameof(QylTelemetryOptions.CollectorProtocol),
                options.CollectorProtocol, "CollectorProtocol must be Grpc or HttpProtobuf.");

        builder.Services.AddSingleton<QylRegistration>();

        QylAutoInstrumentationBootstrap.Boot();
        builder.Services.AddQylAspNetCoreInstrumentation();

        // A build-time host exports nothing, whatever the environment says: no exporter, no probe.
        var buildTimeHost = Array.IndexOf(BuildTimeHostApplicationNames, builder.Environment.ApplicationName) >= 0;

        // The exporter honors the standard OTLP environment variables on its own; discovery only
        // fills the gap when neither the app nor the environment configured an endpoint.
        var endpointFromEnvironment =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OtlpEndpointVariable));
        var endpoint = options.CollectorEndpoint is { } explicitEndpoint
            ? new CollectorDestination(explicitEndpoint, options.CollectorProtocol)
            : null;
        var probe = !buildTimeHost && endpoint is null && options.EnableCollectorDiscovery && !endpointFromEnvironment
            ? CollectorDiscovery.Start()
            : null;

        // RequireConfiguredEndpoint is the one caller that needs the answer now: it means "no
        // endpoint, no export", and the exporter is either registered or it is not. Everyone else
        // gets the probe's answer in the exporter's options callback, which the SDK invokes when it
        // builds the pipeline — so AddQyl returns without touching a socket.
        if (probe is not null && options.RequireConfiguredEndpoint)
            endpoint = CollectorDiscovery.WaitForEndpoint(probe, DiscoveryTimeout);

        // A null endpoint is not the same as "no destination": the exporter still reads the standard
        // environment variables, and failing that falls back to its own localhost default. Only a
        // caller that is itself a telemetry destination cares about the difference, and for it that
        // fallback points at its own ingest port — so it opts out of exporting entirely.
        var exportEnabled = !buildTimeHost
                            && (!options.RequireConfiguredEndpoint || endpoint is not null || endpointFromEnvironment);
        var resolvedEndpoint = endpoint;
        CollectorDestination? ResolveEndpoint()
            => resolvedEndpoint ??= probe is null ? null : CollectorDiscovery.WaitForEndpoint(probe, DiscoveryTimeout);

        var serviceName = options.ServiceName
                          ?? Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
                          ?? builder.Environment.ApplicationName;

        // The resource is derived, not typed: the version is the consuming build's own (the generated
        // QylConsumerBuild registers it before Main) and the environment is the host's.
        var serviceVersion = options.ServiceVersion ?? QylConsumerBuildInfo.Version;
        var deploymentEnvironment = options.DeploymentEnvironment ?? builder.Environment.EnvironmentName;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                resource.AddService(serviceName, serviceNamespace: options.ServiceNamespace, serviceVersion: serviceVersion);
                resource.AddAttributes([new KeyValuePair<string, object>(DeploymentAttributes.EnvironmentName, deploymentEnvironment)]);
            })
            .WithTracing(tracing =>
            {
                var subscribedSources = QylTelemetrySources.GetEnabledActivitySourceNames();
                tracing.AddSource(subscribedSources);

                // A consumer's AdditionalSources are deduplicated against what qyl already
                // subscribed. Naming a source qyl owns — "System.Net.Http" is the one a consumer
                // reaches for — must not register it twice.
                var additionalSources = new HashSet<string>(subscribedSources, StringComparer.Ordinal);
                foreach (var source in options.AdditionalSources)
                {
                    if (additionalSources.Add(source))
                        tracing.AddSource(source);
                }

                var nativeSourceRows = QylTelemetrySources.GetEnabledNativeSourceRows();
                tracing.AddProcessor(new QylNativeSpanProcessor(nativeSourceRows));

                var capture = QylAutoInstrumentationOptions.Current;
                if (capture.IsInstrumentationEnabled(QylAutoInstrumentationSignal.Traces, QylAutoInstrumentationIds.HttpClient) &&
                    (capture.HttpClientCapturedRequestHeaderMap.Count > 0 || capture.HttpClientCapturedResponseHeaderMap.Count > 0) ||
                    capture.IsInstrumentationEnabled(QylAutoInstrumentationSignal.Traces, QylAutoInstrumentationIds.GrpcNetClient) &&
                    (capture.GrpcCapturedRequestMetadataMap.Count > 0 || capture.GrpcCapturedResponseMetadataMap.Count > 0))
                {
                    tracing.AddInstrumentation(services => new QylClientHeaderCapture(capture,
                        services.GetRequiredService<ILogger<QylClientHeaderCapture>>()));
                }

                if (options.EnableSessionTagInheritance)
                    tracing.AddProcessor(new QylSessionSpanProcessor());

                options.ConfigureTracing?.Invoke(tracing);

                if (exportEnabled)
                    tracing.AddOtlpExporter(exporter => ConfigureExporter(exporter, ResolveEndpoint(), "/v1/traces"));
            });

        if (options.EnableMetricsExport)
        {
            builder.Services.AddOpenTelemetry().WithMetrics(metrics =>
            {
                // The full qyl meter inventory (ASP.NET Core, HttpClient, DNS, database,
                // messaging, runtime — honoring per-signal instrumentation options) plus the
                // GenAI meters, which are emitted by the same UseOpenTelemetry() opt-in that
                // produces the gen_ai spans.
                metrics.AddMeter(QylMetricMeters.GetEnabledMeterNames());
                metrics.AddMeter(QylTelemetrySources.GetEnabledMeterNames());

                var additionalMeters = new HashSet<string>(
                    QylMetricMeters.GetEnabledMeterNames().Concat(QylTelemetrySources.GetEnabledMeterNames()),
                    StringComparer.Ordinal);
                foreach (var meter in options.AdditionalMeters)
                {
                    if (additionalMeters.Add(meter))
                        metrics.AddMeter(meter);
                }

                if (exportEnabled)
                    metrics.AddOtlpExporter(exporter => ConfigureExporter(exporter, ResolveEndpoint(), "/v1/metrics"));
            });
        }

        // OTEL_DOTNET_AUTO_LOGS_INSTRUMENTATION_ENABLED is the upstream global logs control; the
        // OpenTelemetry ILogger provider is the only thing qyl binds it to, so it gates registration.
        if (options.EnableLogExport && QylTelemetrySources.IsLogRecordCaptureEnabled())
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;

                if (exportEnabled)
                    logging.AddOtlpExporter(exporter => ConfigureExporter(exporter, ResolveEndpoint(), "/v1/logs"));
            });
        }

        return builder;
    }

    /// <summary>Marks the container as already carrying a qyl registration.</summary>
    private sealed class QylRegistration;

    private static void ConfigureExporter(OtlpExporterOptions exporter, CollectorDestination? destination, string signalPath)
    {
        if (destination is null)
            return;

        // A port does not identify an OTLP transport. Explicit endpoints use the caller's
        // protocol; discovery carries the protocol of its declared target. With no endpoint, leave the SDK's environment
        // configuration intact, including its per-signal endpoint and protocol variables.
        exporter.Protocol = destination.Protocol;
        exporter.Endpoint = destination.Protocol == OtlpExportProtocol.Grpc
            ? destination.Endpoint
            : new UriBuilder(destination.Endpoint) { Path = destination.Endpoint.AbsolutePath.TrimEnd('/') + signalPath }.Uri;
    }
}
