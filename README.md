# Qyl.Telemetry.AutoInstrumentation

Managed automatic instrumentation for .NET 10 applications, including NativeAOT consumers. It
works through compiler-generated Roslyn interceptors, `ActivitySource` subscription, public
`DiagnosticListener` hooks, build assets and module-initializer bootstrap. There is no CLR
profiler, no startup hook, no ReJIT, no runtime IL rewriting and no dynamic plugin loading.

qyl owns this instrumentation implementation and releases it independently. The upstream .NET
automatic-instrumentation contract is a coverage comparison, not a package dependency.
`Qyl.Telemetry.Hosting` uses the OpenTelemetry SDK and OTLP exporter to process and transport
the resulting telemetry; convention bindings come from qyl's own generated registry packages.

Roslyn interceptors are supported by this repository's .NET SDK 10.0.402. See the official
[`interceptors.md`](https://github.com/dotnet/roslyn/blob/main/docs/features/interceptors.md)
contract.

Every attribute key, attribute value and telemetry scope name this package writes is a generated
constant from `Qyl.Telemetry.SemanticConventions`, at the line `Directory.Packages.props` pins. The
instrumentation uses those constants for its own attributes. It preserves vendor attributes and
deprecated keys for the collector. Two known producer representations are converted before export:
Microsoft.Extensions.AI's JSON finish-reason string becomes a string array, and MassTransit's
message-size string becomes an exact nonnegative Int64. Unparseable values remain visible to the
registry checks; no duration unit is inferred or converted.

## Packages

| Package | What it does |
| --- | --- |
| `Qyl.Telemetry.Hosting` | One-line onboarding. `builder.AddQyl()` wires the OpenTelemetry SDK, subscribes the native `ActivitySource` table and the application scope, adds the two processors, registers the meter inventory and exports OTLP with collector discovery. Its package root carries `SKILL.md`, the guide for a coding agent in the consuming application. |
| `Qyl.Telemetry.AutoInstrumentation` | The core runtime: the single qyl `ActivitySource`, the interceptor declarations and helper bodies, the options surface, the compiler-facing ABI, the build assets and the source generator. |
| `Qyl.Telemetry.AutoInstrumentation.Hosting` | Process bootstrap: a `[ModuleInitializer]` activates the qyl listeners when the assembly loads. `AddQyl()` is the application host entrypoint. |
| `Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners` | The base `DiagnosticListener` subscriber and the shared semantics helpers the EF Core and SqlClient packages build on. |
| `Qyl.Telemetry.AutoInstrumentation.EntityFrameworkCore` | EF Core `DiagnosticSource` instrumentation. |
| `Qyl.Telemetry.AutoInstrumentation.SqlClient` | `Microsoft.Data.SqlClient` `DiagnosticSource` instrumentation. |

Add the package that owns the integration you need; the supported zero-configuration consumer path
is a `PackageReference`, and build and analyzer assets flow through NuGet.

```bash
dotnet add package Qyl.Telemetry.Hosting
```

```csharp
using Qyl;

builder.AddQyl();
```

That consumer publishes NativeAOT warning-free — the package contributes no trim, AOT or analyzer
warning to the publish:

```bash
dotnet publish -r <rid> -p:PublishAot=true
```

`AddQyl()` activates the qyl listeners; registers the application scope `Qyl.Telemetry.Application`
behind `Qyl.Spans.Source` (the generated starters and `Spans.Work` start on it, so application code
creates no source and registers nothing), the qyl `ActivitySource`, the framework-native
`Microsoft.AspNetCore` and `System.Net.Http` sources, the version-pinned GenAI, MCP, Azure SDK and
CoreWCF sources and every enabled row of the native-source table; adds the ASP.NET Core enrichment
middleware, `QylNativeSpanProcessor` and `QylSessionSpanProcessor`; registers the meter inventory;
and exports traces, metrics and logs over OTLP — to `OTEL_EXPORTER_OTLP_ENDPOINT` when it is set, otherwise to
`QYL_ENDPOINT` when that is set, otherwise to a qyl collector discovered on `localhost` or the host
`qyl` at 4318/4317. `QYL_ENDPOINT` names a collector for the qyl exporters alone, where the
standard variable would redirect every OTLP exporter in the process.

There is no per-integration enable step: `AddQyl()` subscribes every enabled row of the
native-source table below, and every `[QylIntercept]` declaration is compiled into the consumer's
build by the package's own analyzer and build assets. An integration is on unless the
`OTEL_DOTNET_AUTO_*_INSTRUMENTATION_ENABLED` toggle for its instrumentation id turns it off —
those default to enabled, and they are the only switch.

An application that wires the SDK itself references
`Qyl.Telemetry.AutoInstrumentation.Hosting` alongside `OpenTelemetry.Extensions.Hosting` and
`OpenTelemetry.Exporter.OpenTelemetryProtocol`, and registers the sources by hand:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("my-service"))
    .WithTracing(t => t
        .AddSource("Qyl.Telemetry.AutoInstrumentation", "Qyl.Telemetry.Application")
        .AddOtlpExporter());
builder.Logging.AddOpenTelemetry(o => o.AddOtlpExporter());
```

The instrumentation packages ship no exporter of their own, and a manually wired application that
subscribes a native source qyl also owns exports the same operation twice.

## See a span without a collector

No package here ships a console exporter, and neither does the OpenTelemetry SDK
`Qyl.Telemetry.Hosting` pulls in. Add OpenTelemetry's own, hand it to the `ConfigureTracing` hook
`AddQyl()` exposes, and run:

```bash
dotnet add package OpenTelemetry.Exporter.Console
```

```csharp
builder.AddQyl(o => o.ConfigureTracing = t => t.AddConsoleExporter());
```

```bash
dotnet run
```

The console exporter writes each activity as it ends, so nothing has to be listening. One
`HttpClient` call prints exactly one client span, the framework's, under the `System.Net.Http`
scope and carrying the `qyl.instrumentation.domain` the processor stamped on it. The same span
reaches `weaver registry live-check`, which listens for
OTLP/gRPC on 4317 and prints every span it receives, when `OTEL_EXPORTER_OTLP_ENDPOINT` points at it
— judged against the qyl registry, as the [live check](#live-check) below runs it, because the
upstream registry alone does not declare `qyl.instrumentation.domain`.

## How a library gets instrumented

One question decides the mechanism:

> Does `AddSource("<name>")` alone deliver spans — without a contrib package and without a
> `DiagnosticListener` adapter?
>
> - **Yes** → the source name goes in `QylTelemetrySources`, `AddQyl()` subscribes it, and
>   `QylNativeSpanProcessor` stamps the qyl attributes onto the library's own spans.
> - **No** → a Roslyn source interceptor in `Qyl.Telemetry.AutoInstrumentation`.

A library that emits only `DiagnosticSource`/`DiagnosticListener` events counts as No: an adapter is
a contrib package by another name. A library whose `ActivitySource` stays silent until the consumer
opts in *in their own code* still takes source + processor — qyl does not call the opt-in on the
application's behalf, because an interceptor that did would be the mechanism this rule removes.
`QylTelemetrySources` reads every source name from `QylTelemetryNames` (the vendor sources and
the scopes), so a name missing from the pin is a semantic-convention release blocker rather than a
string literal. One name is still typed by hand: `Experimental.ModelContextProtocol`, which the
registry does not declare yet.

### Subscribed native sources

One row per entry of `QylTelemetrySources.NativeSourceRows`. The row carries the source name, the
instrumentation id whose toggle gates it, and the `qyl.instrumentation.domain` value the processor
stamps. `Azure.*` matches by prefix — the Azure SDK publishes one source per service — and every
other row is an ordinal exact match.

| Library | Pinned | `ActivitySource` | Instrumentation id | Domain |
| --- | --- | --- | --- | --- |
| Azure SDK | `Azure.Storage.Blobs` `12.30.1` | `Azure.*` | `AZURE` | `azure.sdk` |
| CoreWCF | `CoreWCF.Http` `1.9.1` | `CoreWCF.Primitives` | `WCFCORE` | none — see below |
| HttpClient | `System.Net.Http` (BCL) | `System.Net.Http` | `HTTPCLIENT` | `http.client` |
| MySql.Data | `26.7.0` | `connector-net` | `MYSQLDATA` | `db.client` |
| Elastic.Transport, Elastic.Clients.Elasticsearch | `1.1.0`, `9.5.3` | `Elastic.Transport` | `ELASTICTRANSPORT` | `elastic.transport` |
| GraphQL.NET | `8.8.5` | `GraphQL` | `GRAPHQL` | `graphql` |
| Grpc.Net.Client | `2.84.0` | `Grpc.Net.Client` | `GRPCNETCLIENT` | `rpc.grpc` |
| MassTransit | `[8.5.11,9.0.0)` | `MassTransit` | `MASSTRANSIT` | `messaging.masstransit` |
| MongoDB.Driver | `3.12.0` | `MongoDB.Driver` | `MONGODB` | `db.mongodb` |
| MySqlConnector | `2.6.2` | `MySqlConnector` | `MYSQLCONNECTOR` | `db.client` |
| Npgsql | `10.0.3` | `Npgsql` | `NPGSQL` | `db.client` |
| NServiceBus | `10.2.9` | `NServiceBus.Core` | `NSERVICEBUS` | `messaging.nservicebus` |
| Oracle.ManagedDataAccess.Core | `23.26.301` | `Oracle.ManagedDataAccess.Core` | `ORACLEMDA` | `db.client` |
| Quartz.NET | `4.4.0` | `Quartz` | `QUARTZ` | `job.quartz` |
| RabbitMQ.Client | `7.2.2` | `RabbitMQ.Client.Publisher` | `RABBITMQ` | `messaging.rabbitmq` |
| RabbitMQ.Client | `7.2.2` | `RabbitMQ.Client.Subscriber` | `RABBITMQ` | `messaging.rabbitmq` |

Quartz 4 also publishes native metrics on its `Quartz` meter. `AddQyl` subscribes by default;
`OTEL_DOTNET_AUTO_METRICS_QUARTZ_INSTRUMENTATION_ENABLED` controls that subscription independently
of Quartz traces, with the usual global metrics setting as its default.

The Elasticsearch client owns no `ActivitySource`; it enriches Elastic.Transport's, so the two
integrations share one row and one domain. CoreWCF's row carries no domain: those spans are the WCF
*server* side and the registry publishes no instrumentation-domain value for it —
`rpc.wcf.client` belongs to the intercepted client. That is a semantic-convention gap, not a value
to invent here, so the row exists for its `AddSource` call alone.

`QylTelemetrySources` also subscribes sources that need no processor row, because the library
already writes the semantic conventions and qyl adds nothing: `System.Net.Http`,
`Experimental.Microsoft.Extensions.AI`, `Experimental.Microsoft.Agents.AI`,
`Microsoft.Agents.AI.Workflows` and `Experimental.ModelContextProtocol`. Three of the AI paths need
the consumer's own opt-in —
`chatClient.AsBuilder().UseOpenTelemetry().Build()` for `Microsoft.Extensions.AI` 10.10.0,
`agent.AsBuilder().UseOpenTelemetry().Build()` for `Microsoft.Agents.AI` 1.24.0, and
`WorkflowBuilder.WithOpenTelemetry()` for `Microsoft.Agents.AI.Workflows` 1.24.0. `ModelContextProtocol`
2.2.0 and CoreWCF emit without one. MCP *metrics* are deliberately not registered: the official
instruments carry dynamic tool and resource names as dimensions, which conflicts with qyl's
bounded-cardinality policy.

### Roslyn interceptors

One row per `[QylIntercept]` declaration in `src/Qyl.Telemetry.AutoInstrumentation`.

| Integration | Pinned | Intercepted receiver | Instrumentation id | Domain |
| --- | --- | --- | --- | --- |
| ADO.NET | `System.Data.Common` | `System.Data.Common.DbCommand` | `ADONET`, fanned out to `SQLCLIENT` and `SQLITE` by the receiver's namespace | `db.client` |
| Confluent.Kafka | `2.16.0` | `Confluent.Kafka.IProducer<TKey, TValue>` and `IConsumer<TKey, TValue>` | `KAFKA` | `messaging.kafka` |
| StackExchange.Redis | `3.4.0` | `StackExchange.Redis.IDatabaseAsync` | `STACKEXCHANGEREDIS` | `db.redis` |
| WCF client | `System.ServiceModel.Primitives` `10.0.652802` | `System.ServiceModel.ClientBase<TChannel>` | `WCFCLIENT` | `rpc.wcf.client` |

`System.Data.Common`, `Microsoft.Data.SqlClient` 7.1.1, `Microsoft.Data.Sqlite` 10.0.12,
`Confluent.Kafka` 2.16.0, `StackExchange.Redis` 3.4.0 and `System.ServiceModel.*` declare no
`ActivitySource` at the pinned version, so the rule puts them here. There is no longer an
integration in this table whose library owns a native source: the four database providers that did
— Npgsql, MySqlConnector, MySql.Data and Oracle ODP.NET — moved to the table above in 15.0.0, and
the ADO.NET declaration names them in `NativeSourceReceivers`, so a call site on one of those
receivers emits no interceptor and reports no `QYL1001`. It is not a shape mismatch; it is a lane
that belongs to the library.

### The libraries whose telemetry the consumer turns on

Three rows of the native-source table stay silent, or stay poorer, until the application makes a
call in its own code. qyl never makes that call: injecting a consumer's opt-in would be the
interception this package family exists to remove, and it would turn on telemetry the application
did not ask for. The source generator reports `QYL1002` when a compilation uses the library and
never makes the call: at Info for GraphQL.NET and ODP.NET, at Warning for the three GenAI libraries,
whose spans never exist without it (a chat call, `AIAgent.RunAsync` or `WorkflowBuilder.Build`
without `UseOpenTelemetry()` / `WithOpenTelemetry()`).

| Library | The call | Without it |
| --- | --- | --- |
| GraphQL.NET | `IGraphQLBuilder.UseTelemetry()` | the `GraphQL` `ActivitySource` stays silent: no spans at all |
| Oracle ODP.NET | `TracerProviderBuilder.AddOracleDataProviderInstrumentation()` (package `Oracle.ManagedDataAccess.OpenTelemetry`) | the command spans still arrive, carrying only `db.system`, `db.odp.roundtrip.count`, `db.odp.roundtrip.duration` and `db.response.returned_rows`; `db.name`, `db.user`, `db.statement`, `server.address`, `server.port`, `db.odp.connection.id`, `db.odp.sql_id` and exception recording stay off |
| Microsoft.Extensions.AI, Microsoft.Agents.AI, Workflows | `UseOpenTelemetry()` / `WithOpenTelemetry()` | the GenAI sources stay silent |

**MySql.Data needs nothing.** `MySql.Data.OpenTelemetry`'s `AddConnectorNet()` is
`builder.AddSource("connector-net")` and nothing else, which is exactly what `AddQyl()` already
does, so there is no opt-in for a consumer to add and no `QYL1002` row for it.

**MySql.Data sends the statement text unmasked.** Its `db.statement` carries the full command text
on every span, unconditionally, with no option to turn it off — `OTEL_SEMCONV_STABILITY_OPT_IN` is a
no-op for it and qyl does not strip what a library wrote. The value leaves the process; masking it
is the collector's job.

### ASP.NET Core: one server span, and it is the framework's

ASP.NET Core is the third shape. Its `Microsoft.AspNetCore` source is native, so `AddQyl()`
subscribes it and `Microsoft.AspNetCore.Hosting.HttpRequestIn` is the one HTTP SERVER span of a
request — qyl starts none of its own. But unlike every library in the table above, the runtime
creates that activity *empty*: measured on .NET 10.0.11 it carries no tag at all and keeps its raw
operation name. So enrichment is not a processor row here. `AddQylAspNetCoreInstrumentation()`,
which `AddQyl()` calls, registers an `IStartupFilter` whose middleware writes onto the hosting
activity, from the `HttpContext`: `qyl.instrumentation.domain`, `http.request.method`, `url.scheme`,
`url.path`, `url.query` under the redaction control, `http.route` and the `{method} {route}` span
name after routing, `http.response.status_code`, `error.type` on failure, and the configured request
and response headers. Each write fills only what is absent, so a runtime that starts setting these
itself takes them over without a change here. A client that disconnects before anything was sent is
Kestrel's 499, not a failure: the span carries that code, no `error.type`, and its status stays unset.

Two integrations use a framework's public `DiagnosticListener` hook instead of either mechanism:
EF Core through `.EntityFrameworkCore` and `Microsoft.Data.SqlClient` through `.SqlClient`. Neither
library declares an `ActivitySource`, so a listener is the only managed hook there is. No signal has
two qyl lanes any more, which is why there is no arbitration between them: 15.0.0 deleted the last
pair, HttpClient's.

### Toggles

Every row above, native or intercepted, is gated per signal by its instrumentation id:
`OTEL_DOTNET_AUTO_{TRACES|METRICS|LOGS}_<ID>_INSTRUMENTATION_ENABLED`, with the per-signal
`OTEL_DOTNET_AUTO_{TRACES|METRICS|LOGS}_INSTRUMENTATION_ENABLED` and the global
`OTEL_DOTNET_AUTO_INSTRUMENTATION_ENABLED` taking precedence. `AddQyl()` reads the same options, so
a disabled id contributes neither an `AddSource` call nor a processor row. Query-text capture is
opt-in per provider — `OTEL_DOTNET_AUTO_ENTITYFRAMEWORKCORE_SET_DBSTATEMENT_FOR_TEXT` and
`OTEL_DOTNET_AUTO_SQLCLIENT_SET_DBSTATEMENT_FOR_TEXT`, both still read by qyl's own
instrumentation. ASP.NET Core header capture and the local outbound HTTP implementation are opt-in.
gRPC-client metadata capture is independently opt-in; the unconfigured lane still expects no metadata.
The GraphQL and ODP.NET equivalents are no longer qyl's: the document is owned by GraphQL.NET's
`UseTelemetry(o => o.RecordDocument)` and the statement text by ODP.NET's own `SetDbStatementForText`
add-on option, so `OTEL_DOTNET_AUTO_GRAPHQL_SET_DOCUMENT` and
`OTEL_DOTNET_AUTO_ORACLEMDA_SET_DBSTATEMENT_FOR_TEXT` have no reader here.

qyl never force-registers a library's own `Meter`. A consumer that wants `Npgsql`'s or
`NServiceBus.Core`'s native instruments exported registers them through
`OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES` or `QylTelemetryOptions.AdditionalMeters`.

The full 70-row contract, with every environment control and instrumentation option, its evidence
level and its authoritative source, is the generated
[coverage matrix](contracts/coverage-matrix.md).

### Selected outbound HTTP headers

Capture maintenance checkpoint (2026-10-17): all six 28.1.0 packages are published
and indexed on NuGet. The release verifier passed managed and Native AOT
published-package consumers and refreshed the qyl.at version snapshot; the
release is `VERIFIED`. Qyl's existing capture helpers enrich native activities;
the SDK owns the disposable listener through `AddInstrumentation`, without a
CLR profiler. Native MSTest class initialization publishes each consumer once,
and 20 individually selectable data rows execute separate processes using shared
profile constants, explicit expectations and native cancellation. A non-packable
fixture contract assembly keeps the parent and children on the same argument
contract. Native `ArgumentsDisplayName` labels retain the test method and show
the profile without dumping its configuration arguments. Each child asserts
that dynamic code is unavailable, that a parent exists when client spans are
expected, and that every expected HTTP path or gRPC method appears exactly once
with the expected parent. The 20 native scenarios and five directly affected
capture tests pass. A controlled missing-parent probe rejects all 12 active
profiles, and the restored fixtures pass. The Contracts check passes, and scoped
JetBrains inspections have no remaining findings. The private .NET/gRPC payload
bindings remain version-sensitive; no public replacement preserving these
capture boundaries has been established.

Set `OTEL_DOTNET_AUTO_TRACES_HTTP_INSTRUMENTATION_CAPTURE_REQUEST_HEADERS` and/or
`OTEL_DOTNET_AUTO_TRACES_HTTP_INSTRUMENTATION_CAPTURE_RESPONSE_HEADERS` to comma-separated names.
Absent, empty and whitespace-only lists capture nothing. Selection is case-insensitive; emitted
`http.request.header.<name>` / `http.response.header.<name>` keys are lowercase, preserving punctuation,
and values are string arrays. Message headers and content headers are both checked through `NonValidated`.
That API has already converted null header inputs to empty strings; capture preserves its exposed values.

The request is a snapshot at the native start callback, before runtime propagation and later handlers
may add or compute headers. The response is captured synchronously before native span stop/export;
transport failures keep available request fields and omit an absent response. No body is read or buffered.
Capture follows the existing HTTP/global controls, sampling and SDK suppression and ends with provider disposal.
The fixed .NET 10 diagnostic-payload accessors and complete configured consumer have been published and
executed as NativeAOT on .NET 10.0.12. They depend on that runtime's private payload shapes; incompatible
payload access is reported as a bounded warning without including values or exception text.

Capture opt-in selects the exposed fields. The canonical Collector policy retains valid captured names,
including credential-like names; unrelated attributes retain their existing persistence rules.


### Selected gRPC client metadata

Set `OTEL_DOTNET_AUTO_TRACES_GRPCNETCLIENT_INSTRUMENTATION_CAPTURE_REQUEST_METADATA` and/or
`OTEL_DOTNET_AUTO_TRACES_GRPCNETCLIENT_INSTRUMENTATION_CAPTURE_RESPONSE_METADATA` to comma-separated
names. The lists are independent of HTTP capture and each other; absent, empty and whitespace-only lists
capture nothing. Matching is case-insensitive, suffixes become lowercase without changing punctuation,
and values are real string arrays. Selected `-bin` entries retain the HTTP transport's base64-encoded strings,
including values in initial response headers followed by trailers.

The existing `Grpc.Net.Client` activity is enriched through its synchronous diagnostic callbacks.
Request capture reads `HttpRequestMessage.Headers` at native start, before call credentials or later
handlers may add fields. At natural call completion, response capture reads initial headers followed
by trailers and sets one array per name before native stop/export. It preserves the values exposed by
`NonValidated`, including duplicates and empty strings; it cannot reconstruct original metadata entry
boundaries if another layer combined them. It does not read, drain or buffer response bodies.
Cancellation and failure capture only available metadata, without inventing trailers or an absent response.

Configured unary, streaming, cancellation and connection-failure calls were published and executed under
NativeAOT with Grpc.Net.Client 2.84.0 and .NET 10.0.12. The fixed payload accessors depend on that library's
private diagnostic shapes. An incompatible payload produces a bounded, value-free warning. Native
source, operation, kind, status and parentage remain unchanged; metadata is not added to HTTP child spans.
Existing gRPC/global controls, sampling, suppression and provider lifetime still apply.

## The qyl attributes

`qyl.instrumentation.domain` is the only qyl-owned attribute the instrumentation writes onto a
span; applications add native typed attributes with `span.SetTag(key, value)`, using their own names.
`QylNativeSpanProcessor` writes it onto the spans of the native-source table, and the ASP.NET Core
middleware writes it onto the hosting activity, which is the one native span whose own library
writes nothing. Other values come from the library, with the two explicit wire-type conversions
described above. The domain is what the qyl collector's dashboard classifies on,
together with the semantic-convention keys the library emits. Application-owned sources and
attributes remain valid without a qyl-specific prefix.

Its value set is registry-owned (`QylAttributes.InstrumentationDomainValues`): `app`,
`aspnetcore.server`, `azure.sdk`, `db.client`, `db.efcore`, `db.elasticsearch`, `db.mongodb`,
`db.redis`, `db.sqlclient`, `elastic.transport`, `gen_ai.execute_tool`, `gen_ai.invoke_agent`,
`graphql`, `http.client`, `job.quartz`, `messaging.kafka`, `messaging.masstransit`,
`messaging.nservicebus`, `messaging.process`, `messaging.rabbitmq`, `rpc.grpc` and
`rpc.wcf.client`. `app`, `messaging.process`, `gen_ai.invoke_agent` and `gen_ai.execute_tool` are
the application's: the generated starters and `Spans.Work` stamp them.

`session.id` is the session spine, and qyl moves it two ways. The ASP.NET Core middleware that
`AddQyl()` registers stamps the request's W3C `baggage` member `session.id` onto the server span,
admitted only as a session id (unreserved URI characters, at most 128, not dots alone; anything else
is left off), so every ASP.NET Core service joins an agent's session without code. Then
`QylSessionSpanProcessor` copies `session.id` from the nearest tagged in-process ancestor onto
descendant spans that do not carry one. Remote parents and unrelated trace branches propagate
nothing, and the copy happens on end, the last moment the ancestor's tag can be observed.
`demos/Qyl.RealSessionPropagationDemo` and its test, `tests/Qyl.Telemetry.Integration.Tests/Lanes/SessionPropagationTests.cs`,
hold that to three assertions across two real operating-system processes: an in-process descendant
inherits the value, a descendant that already carries one keeps its own, and the server span in
the second process, remote-parented on the same trace, carries the session that reached it in
baggage, which its in-process child inherits.

qyl writes **no baggage**. An outgoing request carries the trace context and whatever baggage the
application itself put on `Activity.Current`, which is what `System.Net.Http` propagates, so the
next process sees the session only if the application put it in baggage on purpose.

qyl's own spans and instruments carry the registry-owned scope names
`Qyl.Telemetry.AutoInstrumentation` (`ActivitySource`) and
`Qyl.Telemetry.AutoInstrumentation.Database` (`Meter`, carrying `db.client.operation.duration`);
the application's spans carry `Qyl.Telemetry.Application`. Both sources carry the qyl registry's
schema URL, `SchemaUrl.Qyl` (`https://qyl.at/schemas/<release>`), so the collector's `schema_urls`
name the registry release each producer compiled against. A manually wired SDK mirrors them in
`AddSource(...)`, `AddMeter(...)` or `OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES`. The generated-code ABI anchor is
`QylGeneratedCodeAbi.V28` in the `Qyl.Telemetry.AutoInstrumentation.GeneratedCode` namespace and
tracks the package major, so a generated interceptor from another major fails to compile rather
than binding to this runtime.

## Analyzers and generator diagnostics

The source generator reports two diagnostics of its own:

| Id | Severity | Reported when |
| --- | --- | --- |
| `QYL1001` | Info | A call site names a declared integration's receiver type and method but its signature does not fit the declared shape, typically because the library changed the signature in a new major. No interceptor is emitted for that call, so it produces no qyl telemetry. Update the declaration or pin the library to a version the shape describes. |
| `QYL1002` | Info; Warning for the GenAI rows | The compilation uses a library whose native `ActivitySource` qyl subscribes but never makes the call that library needs before it emits, or before it emits in full — see [the opt-in table](#the-libraries-whose-telemetry-the-consumer-turns-on). The message names the call and what is lost without it. |

Skipping in silence would hide the loss of instrumentation, and emitting an interceptor with a
mismatched signature would break the consumer's build; the diagnostic is the third option.

`Qyl.Telemetry.AutoInstrumentation` depends on `Qyl.Telemetry.SemanticConventions.Analyzers` (same
pinned line) with `PrivateAssets="none"`, so the `QYL0xxx` rules reach every consumer through
`Qyl.Telemetry.Hosting` with nothing else to install. Among them: `QYL0112` (a constant
`Spans.Work` name), `QYL0110` (a rethrow inside a starter's scope calls `RecordFailure`) and
`QYL0120` (no duplicate tracer instrumentation next to `AddQyl`). Applications may use native
`ActivitySource` instances and typed `SetTag` attributes. A library that version-locks to the
incubating tier sets `OtelSemConvInstrumentationLibrary=true` to acknowledge QYL0008. The rules
are owned and documented by the semantic-conventions repository.

## Testbed on Railway

The databases and brokers the demo verifiers connect to run in the Railway project `qyl-testbed`
(region `europe-west4-drams3a`), declared in [`.railway/railway.ts`](.railway/railway.ts), and not
on the developer machine: SQL Server ships no arm64 image and Oracle's takes 6 GB of local disk.
The pattern, generalized for any repository with a Claude Code and a Codex skill to adopt it, is
[ANcpLua/railway-testbed](https://github.com/ANcpLua/railway-testbed).
Eight services run the images the verifiers pinned, one replica each, each reachable only through a
public TCP proxy (no HTTP domain):

| Service | Image | Serves | Limit (vCPU / memory) | Cold wake + query |
| --- | --- | --- | --- | --- |
| `rabbitmq` | `rabbitmq:4.1-alpine` | masstransit, rabbitmq | 1 / 512 MiB | 13 s |
| `kafka` | `apache/kafka:4.1.0` (KRaft, SASL/PLAIN) | kafka | 1 / 1 GiB | 14 s |
| `mongodb` | `mongo:8-noble` | mongodb | 1 / 1 GiB | 1.6 s |
| `redis` | `redis:8-alpine` (requirepass) | redis | 1 / 512 MiB | 1.4 s |
| `postgres` | `postgres:18-alpine` | npgsql | 1 / 512 MiB | 1.4 s |
| `mysql` | `mysql:9` | mysqlconnector, mysqldata | 1 / 1 GiB | 3.5 s |
| `oracle` | `gvenzl/oracle-free:23-slim-faststart` | oraclemda | 2 / 3 GiB | 3.7 s |
| `sqlserver` | `mcr.microsoft.com/mssql/server:2022-latest` | sqlclient | 2 / 2 GiB | 6.1 s |

**Model: sleeping services.** Every service has Railway's Serverless setting (app sleeping) on.
Railway stops a service once it has sent no outbound packet for 5 minutes, sampled, so 5 to 10
minutes after the last client disconnects, and the next connection through its TCP proxy starts it
again. That first connection is reset while the service starts, so a client retries until the
service answers. The wake column is the probe's time from its first attempt to a successful query,
with all eight asleep and woken together from Berlin (2026-10-12, slowest of 2 runs). Sleeping stops
the processes but keeps the container's filesystem: a woken database still holds what was written
before it slept, and only a deploy starts again from the image (a fresh container was ready within
25 s, RabbitMQ the slowest). A verifier therefore creates what it needs idempotently or under a
unique name. SQL Server's start command turns its customer-feedback telemetry off, the one outbound
traffic that would keep it awake.

**Cost.** Railway Pro bills per minute only while a service runs: $10 per GB-month of memory and $20
per vCPU-month, that is $0.000231 per GB-minute and $0.000463 per vCPU-minute. Asleep, the project
costs nothing: there are no volumes and TCP proxies are free, so the idle cost is $0.00 a month. One
full wake of all eight for 15 minutes costs at most $0.10 at the limits above (10 vCPU and 9.5 GiB,
about 10.2 GB: $0.069 + $0.035). At the usage Railway measured on the awake services (5.6 GB,
under 0.1 vCPU) it is about $0.02, which matches what Railway billed for the project's
first 16 awake minutes. The 5 to 10 minutes a service stays awake after the last connection count
toward those 15 minutes.

**Environment-file contract.** `~/.config/vitals/qyl-testbed.env`, owner-only (`chmod 600`),
registered in Vitals as "qyl testbed connection strings (Railway qyl-testbed)". `KEY=value` lines, no
quotes, no `export`; the verifiers read exactly these keys:

```text
QYL_TESTBED_RABBITMQ_URL=amqp://USER:PASS@HOST:PORT
QYL_TESTBED_KAFKA_BOOTSTRAP=HOST:PORT
QYL_TESTBED_KAFKA_USER=...
QYL_TESTBED_KAFKA_PASSWORD=...
QYL_TESTBED_MONGODB_URL=mongodb://USER:PASS@HOST:PORT/?authSource=admin
QYL_TESTBED_REDIS_URL=redis://:PASS@HOST:PORT
QYL_TESTBED_POSTGRES_URL=Host=HOST;Port=PORT;Username=USER;Password=PASS;Database=qyl
QYL_TESTBED_MYSQL_URL=Server=HOST;Port=PORT;User ID=USER;Password=PASS;Database=qyl
QYL_TESTBED_ORACLE_URL=User Id=system;Password=PASS;Data Source=HOST:PORT/FREEPDB1
QYL_TESTBED_SQLSERVER_URL=Server=HOST,PORT;User ID=sa;Password=PASS;TrustServerCertificate=True;Encrypt=True
QYL_TESTBED_WAKE_SECONDS_MAX=SECONDS
```

Kafka's SASL/PLAIN pair is `QYL_TESTBED_KAFKA_USER` and `QYL_TESTBED_KAFKA_PASSWORD` over the proxy,
which is also the address the broker advertises. The Postgres, MySQL, Oracle and SQL Server values
are the Npgsql, MySqlConnector, ODP.NET Core and Microsoft.Data.SqlClient connection-string forms.
`QYL_TESTBED_WAKE_SECONDS_MAX` is how long a client retries a sleeping service before it fails,
now 120: the longest start measured (25 s for a fresh container) with room for a wake on
another Railway host that has to pull the image first. The passwords are 32 random characters that
exist only as Railway variables (`preserve()` in the partial) and as copies in this file.

**Proof.** The probe wakes all eight at once, retries each until it answers or
`QYL_TESTBED_WAKE_SECONDS_MAX` runs out, prints one line per service with its time and exits non-zero
on any failure. Kafka is a produce and consume round trip through the proxy with SASL that also
checks the advertised listener; Oracle is a `SELECT` in `FREEPDB1`; SQL Server is `SELECT @@VERSION`;
the others are a write and read or a `SELECT`. `--verbose` prints every failed attempt.

```bash
bun install --cwd eng/testbed
bun eng/testbed/probe.ts
```

**Recreate.** The project is `.railway/railway.ts` (partial `autoinstrumentation`); change it there
and apply, never in the dashboard. `eng/testbed/provision.sh` creates any missing service, generates
any missing password with `openssl rand` and sets it from stdin, runs `railway config apply --yes`
and writes the env file from the Railway variables; `eng/testbed/provision.sh env` only rewrites the
file. In a fresh checkout, link the project first:

```bash
railway link --project qyl-testbed        # a new project: railway init --name qyl-testbed
eng/testbed/provision.sh
railway config plan --detailed-exit-code  # exits 0 when Railway and the file agree
```

## Demos and verifiers

Thirty demo applications under `demos/` are the runtime evidence. Native MSTest cases in
`tests/Qyl.Telemetry.Integration.Tests/Lanes/<Name>Tests.cs` build and execute them, including
Native AOT where the integration supports it. Each execution owns a `Qyl.Telemetry.Testing`
OTLP receiver. The cases check the application's report against independent expectations, then
compare the complete capture with the reported spans, metric attributes and logs. Full captures
and registry reports remain under `artifacts/integration-tests` and are attached to test results:

```bash
./eng/build.sh Demos --lane quartz
```

`--lane` takes several names (`--lane quartz http-client`); without it all thirty run. Fallout
selects the native test categories and rejects an empty selection. Each child receives its
receiver through `QYL_ENDPOINT`; the test joins the process before reading the capture, after
exporter disposal. No clock window, settling sleep or collector attribute policy filters the
evidence. Run by hand, a demo takes `AddQyl()`'s discovery path and exports to a running `qyl up`.
The ILogger case delays periodic export and requires the first log batch to survive host disposal,
in both managed and Native AOT applications, without a manual flush.

Ten verifiers need a server, and none starts a container: each connects to its service in the
[testbed on Railway](#testbed-on-railway) through `~/.config/vitals/qyl-testbed.env`. `masstransit`
and `rabbitmq` use `QYL_TESTBED_RABBITMQ_URL`, `kafka` uses `QYL_TESTBED_KAFKA_BOOTSTRAP`,
`QYL_TESTBED_KAFKA_USER` and `QYL_TESTBED_KAFKA_PASSWORD`, `mongodb` uses `QYL_TESTBED_MONGODB_URL`,
`redis` uses `QYL_TESTBED_REDIS_URL`, `npgsql` uses `QYL_TESTBED_POSTGRES_URL`, `mysqlconnector` and
`mysqldata` use `QYL_TESTBED_MYSQL_URL`, `oraclemda` uses `QYL_TESTBED_ORACLE_URL`, and `sqlclient`
uses `QYL_TESTBED_SQLSERVER_URL`, so no lane needs a local container engine or an x64 host. The four database
verifiers have needed a real server since 15.0.0: a native source only emits against a real server,
where the old interceptor demos proved themselves against a connectionless exception.

The gate before a push or a release is `./eng/build.sh Check`: the contract invariants and the
generated contract artifacts, the version and documentation pins, the Release builds of the solution
and of the demo solution, the public API report (it rewrites `src/*/PublicAPI.txt` and never fails),
the generator snapshots, the package layout and the consumer behaviors (eight consumers of the
packages this checkout packs, NativeAOT publishes included). `./eng/build.sh CheckFull` adds the
NativeAOT publish matrix, all thirty demo lanes, the live check, the package smoke test, the NativeAOT
web API demo and the OTLP wire evidence. Every target
also runs alone, and `./eng/build.sh --help` lists them with their parameters:

```bash
./eng/build.sh AotPublishGate --aot-set warned
./eng/build.sh TestbedProbe
```

The separate OTLP wire target checks the HTTP request path, content type, typed values and
redaction using official protobuf messages and independent fixed expectations. It complements
the native integration cases. Collector persistence is verified in qyl's integration suite.

## Live check

`./eng/build.sh LiveCheck` runs `weaver registry live-check` as an OTLP/gRPC listener on 4317 and
points nine real demo applications at it, so what the instrumentation actually emitted is judged against the
pinned semantic-convention registry rather than against an assertion written in this repository.
The lanes are one per row of the native-source table — `azure`, `corewcf`, `elastictransport`,
`elasticsearch`, `masstransit`, `mongodb`, `nservicebus`, `quartz`, `rabbitmq` — with RabbitMQ's two
source names on one lane, and Elastic.Transport and Elasticsearch on a lane each because the two
clients put different attributes on the shared source. Weaver judges every span, metric and resource
it receives: the library's keys, the vendor keys it passes through and the
`qyl.instrumentation.domain` the processor stamped.

`--fail-on violation` is the threshold and there is no allowlist in the gate. A finding is closed by
changing what the instrumentation writes or by declaring the key in the registry, never by waving it
through. The lanes must report zero violations against the registry release the pin names.

How a finding is *levelled* is the registry's decision, and it takes two flags that must travel
together:

- `--config <registry>/.weaver.toml` drops weaver's built-in `deprecated`, `type_mismatch` and
  `undefined_enum_variant` findings by id. Those advisors are compiled into the binary and emit at a
  level no policy can lower.
- `--advice-policies <registry>/policies/live_check_advice` re-issues them at the level the registry
  chose: an open enum carrying `_OTHER` is information, a renamed or obsoleted key a library still
  emits is an improvement, a type mismatch whose value parses is an improvement.

Passing only the second changes nothing for those three advisors, and an unreadable path fails
silently, so the gate checks for both halves and refuses to run without them.

The registry is a checkout of
[Qyl.Telemetry.SemanticConventions](https://github.com/ANcpLua/Qyl.Telemetry.SemanticConventions)
at the tag `v` + the `Qyl.Telemetry.SemanticConventions` pin in `Directory.Packages.props`, which the
target clones into `.semconv` and prepares the way the registry's own `scripts/fetch-core.sh` does,
so `registry/manifest.yaml` can resolve its filtered core dependency. The published NuGet packages carry the generated C#, not the registry itself, which
is why the gate needs the source repository. Weaver is the version `mise.toml` pins (`mise install`).
The three broker and database lanes wake their testbed service first, like `Demos` does.
`--live-check-skip <lane>` skips a lane and names the skip in the output as `live-check-partial-ok`:

```bash
./eng/build.sh LiveCheck
./eng/build.sh LiveCheck --live-check-skip mongodb
```

One gap in coverage is recorded rather than hidden: **`RabbitMQ.Client.Subscriber` has no consuming
demo.** `AddQyl()` subscribes both RabbitMQ source names, but the RabbitMQ lane only publishes, so
the subscriber source emits nothing and its `deliver` and `fetch` spans are unjudged by this gate.

## Release

The version lives in `Directory.Build.props` `<Version>`, and the `Qyl.Telemetry.*` family ships as
one line at one version. Bump it, pack the six packages and push them to nuget.org from your machine
with the NuGet API key registered in Vitals, then tag the commit `v<version>` and create the GitHub
release. Nothing runs on a push to `main`: `./eng/build.sh Check` runs before every push and release,
and `./release.sh <version>` publishes (see `SKILL.md`). It packs through `./eng/build.sh Pack`, which
puts the six packages into `artifacts/release`; `./release.sh <version> --dry-run` runs every check,
the real pack and a NuGet key probe for every package id, and publishes nothing.

The package major is the compile-time ABI rather than the product version, which is why it runs
ahead of the rest of qyl: an `X.y` package pairs with `QylGeneratedCodeAbi.V<X>` and nothing else.

One line names the semantic-convention release: the `Qyl.Telemetry.SemanticConventions`
`PackageVersion` in `Directory.Packages.props`. The code compiles against its generated constants,
the live check derives its registry tag from it (`v<version>`), and `./eng/build.sh Contracts` fails
when a tracked file hard-codes a `SEMCONV_REF` or a registry tag that names another release.
The live check also compares the pin against the checkout's `VersionPrefix` and fails when the tag
holds another release — judging spans against a registry other than the one that produced the
constants is the drift that would otherwise look like a green gate.

A consumer can read the release a package compiled against in two places: the nuspec dependency on
`Qyl.Telemetry.SemanticConventions`, whose lower bound is the exact pin, and the assembly metadata of
`Qyl.Telemetry.AutoInstrumentation.dll`,
`[assembly: AssemblyMetadata("QylSemanticConventionsVersion", "<version>")]`, which the build takes
from the resolved reference and `./eng/build.sh PackageLayout` checks against the pin.

## Limitations

- Only source-visible call sites can be intercepted. Calls hidden in compiled dependencies,
  reflection or dynamic dispatch need a public runtime hook or remain unsupported.
- Some integrations are managed-only because the instrumented library requires runtime code
  generation. The NativeAOT boundary is this compile-time/managed substrate; it claims no parity
  with the CLR-profiler OpenTelemetry .NET automatic instrumentor and does not imply that every
  third-party library publishes warning-free under NativeAOT.
- Query text and other sensitive or high-cardinality values remain opt-in or redacted according to
  the package options and the upstream OpenTelemetry controls.
- Generic HTTP header capture records explicitly selected names, including `Mcp-Param-*`.
- Generator snapshots prove emitted source shape; protocol interoperability requires a real OTLP
  receiver and structural decoding of the official protobuf messages.
- MassTransit 9 is commercially licensed and is neither referenced nor redistributed here; the demo
  pin is the Apache-2.0 range `[8.5.11,9.0.0)`.

## License

Apache-2.0
