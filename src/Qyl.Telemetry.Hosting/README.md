# Qyl.Telemetry.Hosting

One-line telemetry for a .NET 10 application, NativeAOT included. `builder.AddQyl()` wires the
OpenTelemetry SDK, activates qyl's automatic instrumentation, registers the activity sources and
meters, and exports traces, metrics and logs over OTLP.

```bash
dotnet add package Qyl.Telemetry.Hosting
```

```csharp
using Qyl;

builder.AddQyl();
```

That is the whole integration. There is no per-library enable step: every supported source is on
unless its `OTEL_DOTNET_AUTO_<ID>_INSTRUMENTATION_ENABLED` toggle turns it off.

The package also carries `SKILL.md`, the guide for a coding agent working in the application: what
`AddQyl()` already records, the generated span starters (`Spans.Work`, `Spans.DbClient`, ...),
native typed attributes for business keys, `RecordFailure`, and the three things the compiler cannot check.

## Your own spans

Use `Qyl.Spans` for the generated application scope, or register an application-owned
`ActivitySource` with `options.AdditionalSources.Add(sourceName)`:

```csharp
using var span = Spans.Work("checkout");
span?.SetTag("checkout.order.id", orderId);
try
{
    await checkout.RunAsync(order);
}
catch (Exception exception)
{
    span?.RecordFailure(exception);
    throw;
}
```

## Where the telemetry goes

`AddQyl()` exports to the first of these that is set:

1. `OTEL_EXPORTER_OTLP_ENDPOINT` — the standard variable, which also redirects every other OTLP
   exporter in the process.
2. `QYL_ENDPOINT` — an HTTP/protobuf collector for the qyl exporters alone. For gRPC, use the standard
   OTLP endpoint/protocol variables or set both `CollectorEndpoint` and `CollectorProtocol`.
3. A qyl collector discovered on `localhost` or the host `qyl` at 4318/4317, which is what
   `qyl up` from the [`qyl`](https://www.nuget.org/packages/qyl) dotnet tool provides.

To see spans without any collector, add OpenTelemetry's console exporter through the tracing hook:

```bash
dotnet add package OpenTelemetry.Exporter.Console
```

```csharp
builder.AddQyl(o => o.ConfigureTracing = t => t.AddConsoleExporter());
```

## Options

`AddQyl(o => ...)` takes a `QylTelemetryOptions`. Every option has a zero-config default.

| Option | Default | Effect |
|---|---|---|
| `ServiceName` | `OTEL_SERVICE_NAME`, else the host application name | The `service.name` resource attribute. |
| `ServiceVersion` | the build's own version | The `service.version` resource attribute. By default the consuming build's `InformationalVersion` with its source revision (`1.4.0+3f2a9c1`), compiled into the application by the package's generator. |
| `ServiceNamespace` | unset | The `service.namespace` resource attribute: the system this service belongs to. |
| `DeploymentEnvironment` | the host environment | The `deployment.environment.name` resource attribute, by default `IHostEnvironment.EnvironmentName`. |
| `ConfigureTracing` | unset | A hook for tracing setup the typed options do not cover. It runs after the qyl sources and processors and before the OTLP exporter. |
| `CollectorEndpoint` | unset | An explicit collector endpoint, ahead of the order above. |
| `CollectorProtocol` | `HttpProtobuf` | Transport for an explicit `CollectorEndpoint`. Set `OtlpExportProtocol.Grpc` for gRPC on any port. Standard OTLP environment endpoints use the SDK's protocol configuration. |
| `EnableCollectorDiscovery` | `true` | Probe `localhost` and the host `qyl` for a collector when no endpoint is set. |
| `RequireConfiguredEndpoint` | `false` | Export only when an endpoint is set or discovered, instead of falling back to the exporter's `localhost` default. A process that is itself a telemetry destination, such as a collector, sets it to `true`. |
| `EnableLogExport` | `true` | Export application logs over OTLP. |
| `EnableMetricsExport` | `true` | Register and export the qyl meter inventory and the GenAI meters. |
| `EnableSessionTagInheritance` | `true` | Copy `session.id` from the nearest in-process ancestor span onto spans that lack it. It sets a span tag only; nothing goes on the wire to the next process. The server span of an ASP.NET Core request carries the `session.id` of the request's W3C `baggage` either way. |
| `AdditionalSources` | empty | More `ActivitySource` names to subscribe. |
| `AdditionalMeters` | empty | More `Meter` names to subscribe. |

## What it pulls in

This package depends on `Qyl.Telemetry.AutoInstrumentation` (the instrumentation runtime) and
`Qyl.Telemetry.AutoInstrumentation.Hosting` (the listener bootstrap). An application that wires
the OpenTelemetry SDK itself should reference `Qyl.Telemetry.AutoInstrumentation.Hosting` directly
and register the `Qyl.Telemetry.AutoInstrumentation` source by hand, instead of this package.

Publishing with `-p:PublishAot=true` produces no trim, AOT or analyzer warning from this package.

## More

- Which libraries are instrumented, the toggles, and the qyl attributes:
  [repository README](https://github.com/ANcpLua/Qyl.Telemetry.AutoInstrumentation#readme)
- Every environment control with its evidence:
  [resolved contract](https://github.com/ANcpLua/Qyl.Telemetry.AutoInstrumentation/blob/main/contracts/generated/qyl-aot-contract.resolved.yaml)
- Changes per version:
  [CHANGELOG](https://github.com/ANcpLua/Qyl.Telemetry.AutoInstrumentation/blob/main/CHANGELOG.md)
- The product around it: [qyl.at](https://qyl.at/)
