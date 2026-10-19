# Changelog

Notable changes to the `Qyl.Telemetry.*` package family. Versions are owned by `<Version>` in
`Directory.Build.props`. The local release gates run before `./release.sh` packs and publishes
that exact version, creates the matching `v*` tag and GitHub release, and waits for all package indexes. Indexed
packages are then exercised in clean managed and NativeAOT consumers.

## [28.2.3] - 2026-10-19

- Consume SemanticConventions 15.0.2 with corrected native-vendor persistence policy and analyzer namespace recognition.

## [28.2.2] - 2026-10-19

- Consume SemanticConventions 15.0.0 and its generated Weaver v2 validation configuration.
- Probe fresh NuGet indexes during release verification instead of accepting stale CDN responses.
- Preserve compiler-host helper exclusions when an isolated NuGet cache has no trailing directory separator.
- Preserve nullable annotations on generated receivers, parameters and returns, including nullable generic arguments and array ranks. Keep overload matching annotation-insensitive.
- Remove blanket warning suppression from generated interceptors and build metadata. Scope obsolete warnings to their forwarded calls while retaining diagnostics at the original caller.
- Leave optional argument defaults with the original method instead of copying them into interceptor declarations.
- Verify generated consumers with warnings as errors in the normal pre-push gate. Exclude unused filesystem guards from the compiler-host generator assembly.

## [28.2.1] - 2026-10-19

- Rebuild the instrumentation and hosting package family with .NET SDK 10.0.402 and ANcpLua.Analyzers 2.1.8. Public API unchanged; no runtime behavior change.

## [28.2.0] - 2026-10-17

- Honor explicitly selected `mcp-param-*` headers and metadata without a hidden prefix exclusion.
- Capture selected gRPC binary metadata as the HTTP transport's base64 string arrays, including initial response headers followed by trailers. Preserve existing native spans and opt-in defaults.
- Verify the binary-only native profile and raw binary header/trailer values. Consume SemanticConventions 14.0.0 with the updated capture persistence policy.

## [28.1.1] - 2026-10-17

- Rebuild the instrumentation and hosting package family against SemanticConventions 13.2.0 so producers and the Collector share the same generated vocabulary and schema version. Preserve the reviewed HTTP/gRPC capture implementation and generated-code ABI V28.

## [28.1.0] - 2026-10-17

- Register qyl-owned client capture as disposable OpenTelemetry instrumentation instead of a lifetime-only span processor. Preserve native span ownership and provider disposal without a profiler.
- Expose each HTTP/gRPC capture profile as a native MSTest data row. Publish once per test class, forward native fixture/invocation cancellation through publishing and child execution, retain row-specific evidence, and share profile/configuration constants.
- Declare capture inputs and expectations in the data rows, share the child-process contract through one non-packable fixture assembly, and require every expected HTTP/gRPC operation exactly once. Use native typed assertions for captured arrays.
- Require a parent activity when native client spans are expected and verify every emitted span's parent unconditionally. Give each native MSTest row a concise profile label through `ArgumentsDisplayName`.
- Assert that each configured consumer actually runs without dynamic-code support; exercise the factory registration through NativeAOT execution.

- Add independently selected textual gRPC client metadata at native start/completion, combining initial headers then trailers without reading streams. Skip binary names and verify configured unary, streaming, cancellation and failure paths through an executed NativeAOT consumer.
- Add selected outbound HTTP request/response headers on the existing native span, including content headers, raw exposed value arrays, start/stop snapshots, sampling/suppression and provider disposal. Verify configured execution through a published NativeAOT consumer.

- Preserve distinct underscore, hyphen and dotted names in explicitly selected ASP.NET Core headers; retain case-insensitive selection and string arrays.
- Run the transport integration tests against Qyl.Telemetry.Testing 3.0.3.
- Check environment configuration through resolved instrumentation decisions and capture-key maps instead of removed metadata properties.

## [28.0.0] - 2026-10-17

- Consume SemanticConventions 13.1.2, including its compact analyzer catalog and shared legacy compiler polyfills.
- Remove the redundant service-collection activation wrapper and the unused DB-command operation-name binding; advance the generated-code ABI to V28.
- Remove metadata-only constructor property storage, the unused integration domain argument, and the obsolete method-name binding. Preserve Roslyn metadata uses with annotations on those exact protocol members.
- Remove redundant internal option state and obsolete empty contract allowlists. Runtime implementation claims now require runtime evidence without an exception list.
- Align source namespaces and folders, simplify confirmed inspection findings, and move negative generator fixtures into native MSTest diagnostics tests.

## [27.0.0] - 2026-10-17

- Adopt SemanticConventions 13.1.1: native typed application attributes replace `SetAppTag` and its forced prefix. Generated starters remain optional; consumers can register their own activity sources.
- Verify both generated and application-owned sources through real packaged consumers and the public host pipeline. Use Testing 3.0.1 for HTTP and gRPC receivers, configurable bounded payloads, reliable cancellation and retained raw captures.
- Add `CollectorProtocol` for explicit collector endpoints. HTTP/protobuf remains the programmatic default; selecting native `OtlpExportProtocol.Grpc` works on any port. Remove the port-4317 transport guess and verify real hosts export all three signals through both transports.
- Advance the generated-code ABI to V27 so generator and runtime packages share the new major.
- Dispose the collector-discovery probe's wait handle, remove unused build helpers and meter constants, and keep process failure diagnostics with their elapsed time.

## [26.0.0] - 2026-10-16

### Changed

- Update the OpenTelemetry SDK, exporters and hosting integration to 1.20.0 for metric concurrency, async suppression and shutdown log delivery fixes.
- Update semantic-convention bindings and analyzers to 12.0.0, including the measured Oracle duration wire type, and Roslyn source utilities to 3.0.0.
- Advance the generated-code ABI to V26; generators and runtime packages must share that major.
- Pin the repository to .NET SDK 10.0.401 and native MSTest 4.5.1 with Microsoft.Testing.Platform.
- Replace the custom demo runner with thirty native MSTest integration cases using Qyl.Telemetry.Testing 1.1.0. Retain complete OTLP captures, original export payloads and registry reports, compare independent application reports, and join child processes before reading their telemetry.
- Remove the ILogger demo's manual-flush workaround and verify first-batch shutdown delivery in managed and Native AOT applications.
- Convert two known producer representations without changing their meaning: Microsoft.Extensions.AI finish-reason JSON to a string array, and MassTransit message-size text to a nonnegative Int64. Preserve malformed and out-of-range values for diagnostics.
- Verify conversion boundaries through the public host pipeline and attribute Oracle child spans by their operation context and parent IDs, preserving background exporter activities in the diagnostic report.
- Remove the unused collector harness, its stale tool pin and the API-contract dependency that only that harness used.
- Remove unused internal HTTP-header and ambient-start helpers; keep the timing paths used by EF Core and SqlClient.

## [25.1.0] - 2026-10-15

### Changed

- Update the shared convention bindings and consumer analyzers to Qyl.Telemetry.SemanticConventions 10.1.0.
- Apply JetBrains cleanup for redundant imports and null suppression, constructor initialization, and private scanner state without changing the public API.

## [25.0.3] - 2026-10-15

### Documentation

- `Qyl.Telemetry.Hosting`'s `qyl-telemetry` skill explains how to export telemetry from services started by `Qyl.Orchestration`: keep `AddQyl()` in each service, pass the receiver's OTLP endpoint and protocol through the child environment, and separate service references and readiness from telemetry setup. It also documents the `OTEL_SERVICE_NAME` default.

## [25.0.2] - 2026-10-15

### Documentation

- `Qyl.Telemetry.Hosting`'s SKILL.md says how an `ActivityListener` of your own selects the application's spans: compare `source.Name` with `QylTelemetryNames.Scopes.QylTelemetryApplication`. Reading `Spans.Source` inside `ShouldListenTo` throws `TypeInitializationException`.

## [25.0.1] - 2026-10-15

### Changed

- Qyl.Telemetry.SemanticConventions 10.0.1. `Qyl.Telemetry.Hosting` carries its analyzers to consumers, so QYL0113 now reports `SetAppTag` keys whose name the registry does not persist (`SetAppTag("user.email", ...)` is `qyl.app.user.email`, dropped like `user.email`). The persistence table also treats plain token counts as token-exempt and four vendor keys as not persisted.

## [25.0.0] - 2026-10-14

The agent-first API of the qyl API DSL ADR (2026-10-14), on `Qyl.Telemetry.SemanticConventions`
10.0.0: a breaking release.

### Added

- `AddQyl()` subscribes the application scope `Qyl.Telemetry.Application` behind `Qyl.Spans.Source`,
  unconditionally: a span from a generated starter or `Spans.Work` is exported with no
  `ActivitySource` and no `AddSource` in application code.

- `AddQyl()` derives the resource (ADR D5): `service.version` defaults to the consuming build's own
  version and source revision (`InformationalVersion`, e.g. `1.4.0+3f2a9c1`), which the package's
  build assets expose to the generator as compiler-visible properties and the generator compiles
  into the application as `QylConsumerBuild` (constants `Version` and `Revision`; its module
  initializer hands the version over, no reflection); `deployment.environment.name` defaults to
  `IHostEnvironment.EnvironmentName`. New options `ServiceNamespace` (`service.namespace`) and
  `DeploymentEnvironment`.
- The session spine: the ASP.NET Core middleware stamps the request's W3C `baggage` member
  `session.id` onto the server span (a session id only: unreserved URI characters, at most 128,
  not dots alone), which `QylSessionSpanProcessor` carries to every in-process descendant. Every
  ASP.NET Core service joins an agent's session without code; `Qyl.Api`'s own filter is redundant.
- `QYL1002` reports, at Warning, `Microsoft.Extensions.AI`, `Microsoft.Agents.AI` and
  `Microsoft.Agents.AI.Workflows` used without `UseOpenTelemetry()` / `WithOpenTelemetry()`: their
  `ActivitySource`s stay silent without it. GraphQL.NET and ODP.NET stay at Info.

- `Qyl.Telemetry.Hosting` carries `SKILL.md` at its package root (ADR D7): the guide for a coding
  agent in the consuming application. It opens with what `AddQyl()` already records, then the
  starters, `Work`, `SetAppTag` and `RecordFailure`, and the three things the compiler cannot see
  (the messaging parent context, `UseOpenTelemetry()` on a custom source name, business keys).
  `./eng/build.sh PackageLayout` checks it is packed.

### Removed

- `QylTelemetryOptions.ResourceAttributes`, the last string-keyed slot: the resource is derived
  (`ServiceName`, `ServiceNamespace`, `ServiceVersion`, `DeploymentEnvironment`), and
  `OTEL_RESOURCE_ATTRIBUTES` still reaches the SDK.

### Changed

- The semantic-convention analyzers flow to every consumer: the core package references
  `Qyl.Telemetry.SemanticConventions.Analyzers` with `PrivateAssets="none"`, so its nuspec
  dependency excludes neither analyzers nor build assets, and a `Qyl.Telemetry.Hosting` consumer gets
  every `QYL0xxx` rule, among them QYL0111 (no hand-made `ActivitySource` or raw `StartActivity` in
  application code) at Error. `./eng/build.sh PackageLayout` checks the dependency;
  `ConsumerBehavior` builds a package consumer whose `new ActivitySource` fails with QYL0111.
- **A failure is recorded with `Spans.RecordFailure`** (ADR D4), the call application code makes: the
  generated interceptors, the asynchronous observer, the ADO.NET command interceptor and the ASP.NET
  Core middleware now set status Error *with the exception's message as the description* (bounded
  to 256 characters; the one message carrier that survives ingest), `error.type`, and the exception
  event. `RecordException` gave neither the description nor the event. `error.type` of a generic
  exception stays bounded (`Confluent.Kafka.ProduceException`2`).
- The generated-code helper `QylInterceptedActivity.RecordException` is `RecordFailure`.
- Span names come from the registry's `qyl.span_name` chains (`Qyl.SpanNames`, generated in the
  semantic-convention package) with the attributes each span records, so interceptors and the
  generated starters name spans the same way; `QylSpanNames` is deleted. One name changes: a
  StackExchange.Redis command span is `{db.operation.name} {db.namespace}`, the database index
  (`GET 0`), where it was the command alone.
- The session-propagation lane's third property is the stamp: the second process's server span
  carries the session that reached it in baggage, with no `Qyl.Api`, and its child inherits it. The
  ASP.NET Core demo drops its own baggage filter and proves the middleware's.
- The Npgsql and HttpClient demos run their failing call inside the application's own `Spans.Work`
  span and assert the `RecordFailure` triple on it; the Kafka demo asserts it on the interceptor's
  failed producer span.
- The qyl `ActivitySource` carries the qyl registry's schema URL (`SchemaUrl.Qyl`,
  `https://qyl.at/schemas/10.0.0`), like the application scope.
- README: the QYL1002 rows it already claimed exist; the one source name still typed by hand
  (`Experimental.ModelContextProtocol`) is named; the domain list is the registry's whole value set,
  `db.elasticsearch` and the four application domains included.
- The ReSharper inspection batch of 2026-10-14 over `src/` (67 findings): redundant usings and
  qualifiers, declarations folded into their null checks, the redundant `AttributeUsage`
  properties, braces, the generator's unused receiver parameter and never-read
  `IntegrationDeclaration.Domain`, the never-read `SqlClientCommand.OperationId`; the ASP.NET Core
  middleware observes a synchronous throw and a faulted task in one async path. No behavior change.
- The ASP.NET Core, MCP and session-propagation demos start their own spans with `Spans.Work`
  instead of a hand-made source added through `AdditionalSources`.
- Every `Qyl.Telemetry.SemanticConventions*` package is pinned at 10.0.0 (`Directory.Packages.props`
  remains the one place that names it).
- The generated-code ABI anchor is `QylGeneratedCodeAbi.V25`; interceptors generated by a 24.x
  generator no longer compile against this runtime.
