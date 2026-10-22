# Qyl.Telemetry.AutoInstrumentation

The instrumentation runtime behind qyl's automatic telemetry for .NET 10 and NativeAOT: the single
qyl `ActivitySource`, the Roslyn interceptor declarations and their helper bodies, the options
surface, the build assets and the source generator that compiles the interceptors into the
consumer's own build. There is no CLR profiler, no startup hook, no ReJIT and no runtime IL
rewriting.

Most applications do not reference this package directly. It arrives through
[`Qyl.Telemetry.Hosting`](https://www.nuget.org/packages/Qyl.Telemetry.Hosting), whose
`builder.AddQyl()` is the supported one-line path:

```bash
dotnet add package Qyl.Telemetry.Hosting
```

Reference this package on its own when the application wires the OpenTelemetry SDK itself and
only needs the instrumentation, together with
[`Qyl.Telemetry.AutoInstrumentation.Hosting`](https://www.nuget.org/packages/Qyl.Telemetry.AutoInstrumentation.Hosting)
for the listener bootstrap:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource("Qyl.Telemetry.AutoInstrumentation")
        .AddOtlpExporter());
```

## What it writes

Every attribute key, value and scope name is a generated constant from
[`Qyl.Telemetry.SemanticConventions`](https://www.nuget.org/packages/Qyl.Telemetry.SemanticConventions).
The instrumentation writes those constants and nothing else; it never renames, drops or coerces
what a library emitted.

Each instrumented library is gated per signal by its instrumentation id:
`OTEL_DOTNET_AUTO_{TRACES|METRICS|LOGS}_<ID>_INSTRUMENTATION_ENABLED`, with the per-signal and the
global `OTEL_DOTNET_AUTO_INSTRUMENTATION_ENABLED` taking precedence. Everything is on by default.

## More

- The instrumented libraries, how interception works, and the qyl attributes:
  [repository README](https://github.com/ANcpLua/Qyl.Telemetry.AutoInstrumentation#readme)
- Every environment control with its evidence:
  [resolved contract](https://github.com/ANcpLua/Qyl.Telemetry.AutoInstrumentation/blob/main/contracts/generated/qyl-aot-contract.resolved.yaml)
- Changes per version:
  [CHANGELOG](https://github.com/ANcpLua/Qyl.Telemetry.AutoInstrumentation/blob/main/CHANGELOG.md)
