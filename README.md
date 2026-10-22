# Qyl.Telemetry.AutoInstrumentation

Managed automatic instrumentation for .NET 10 applications, including NativeAOT consumers. It
works through compiler-generated Roslyn interceptors, `ActivitySource` subscription, public
`DiagnosticListener` hooks, build assets and module-initializer bootstrap. There is no CLR
profiler, no startup hook, no ReJIT, no runtime IL rewriting and no dynamic plugin loading.
Interceptors follow Roslyn's
[`interceptors.md`](https://github.com/dotnet/roslyn/blob/main/docs/features/interceptors.md)
contract and need the .NET SDK `global.json` pins (10.0.402).

The upstream .NET automatic-instrumentation contract is a coverage comparison, not a package
dependency. `Qyl.Telemetry.Hosting` uses the OpenTelemetry SDK and OTLP exporter for processing and
transport.

Every attribute key, attribute value and scope name the instrumentation writes is a generated
constant from `Qyl.Telemetry.SemanticConventions` at the `Directory.Packages.props` pin. Vendor
attributes and deprecated keys pass through to the collector. Two producer representations are
converted before export: Microsoft.Extensions.AI's JSON finish-reason string becomes a string array,
and MassTransit's message-size string becomes a nonnegative Int64. Unparseable values stay as they
are; no duration unit is inferred or converted.

## Packages

| Package | What it does |
| --- | --- |
| `Qyl.Telemetry.Hosting` | One-line onboarding. `builder.AddQyl()` wires the OpenTelemetry SDK, subscribes the native `ActivitySource` table and the application scope, adds the two processors, registers the meter inventory and exports OTLP with collector discovery. Its package root carries `SKILL.md`, the guide for a coding agent in the consuming application. |
| `Qyl.Telemetry.AutoInstrumentation` | The core runtime: the single qyl `ActivitySource`, the interceptor declarations and helper bodies, the options surface, the compiler-facing ABI, the build assets and the source generator. |
| `Qyl.Telemetry.AutoInstrumentation.Hosting` | Process bootstrap: a `[ModuleInitializer]` activates the qyl listeners when the assembly loads. `AddQyl()` is the application host entrypoint. |
| `Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners` | The base `DiagnosticListener` subscriber and the shared semantics helpers the EF Core and SqlClient packages build on. |
| `Qyl.Telemetry.AutoInstrumentation.EntityFrameworkCore` | EF Core `DiagnosticSource` instrumentation. |
| `Qyl.Telemetry.AutoInstrumentation.SqlClient` | `Microsoft.Data.SqlClient` `DiagnosticSource` instrumentation. |

```bash
dotnet add package Qyl.Telemetry.Hosting
```

```csharp
using Qyl;

builder.AddQyl();
```

The consumer publishes NativeAOT without a trim, AOT or analyzer warning from these packages:

```bash
dotnet publish -r <rid> -p:PublishAot=true
```

`AddQyl()` activates the qyl listeners; registers the application scope `Qyl.Telemetry.Application`
behind `Qyl.Spans.Source` (the generated starters and `Spans.Work` start on it), the qyl
`ActivitySource`, the `Microsoft.AspNetCore` and `System.Net.Http` sources, the version-pinned GenAI,
MCP, Azure SDK and CoreWCF sources and every enabled row of the native-source table; adds the
ASP.NET Core enrichment middleware, `QylNativeSpanProcessor` and `QylSessionSpanProcessor`; registers
the meter inventory; and exports traces, metrics and logs over OTLP to `OTEL_EXPORTER_OTLP_ENDPOINT`,
else `QYL_ENDPOINT` (qyl's exporters only), else a qyl collector discovered on `localhost` or the
host `qyl` at 4318/4317. There is no per-integration enable step: every `[QylIntercept]` declaration
compiles into the consumer through the package's analyzer and build assets, and only the
`OTEL_DOTNET_AUTO_*_INSTRUMENTATION_ENABLED` toggles turn an integration off.

An application that wires the SDK itself references `Qyl.Telemetry.AutoInstrumentation.Hosting`,
`OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Exporter.OpenTelemetryProtocol` and registers
the sources by hand. The instrumentation packages ship no exporter; subscribing a native source that
qyl also owns exports the same operation twice.

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("my-service"))
    .WithTracing(t => t
        .AddSource("Qyl.Telemetry.AutoInstrumentation", "Qyl.Telemetry.Application")
        .AddOtlpExporter());
builder.Logging.AddOpenTelemetry(o => o.AddOtlpExporter());
```

## See a span without a collector

```bash
dotnet add package OpenTelemetry.Exporter.Console
```

```csharp
builder.AddQyl(o => o.ConfigureTracing = t => t.AddConsoleExporter());
```

One `HttpClient` call prints one client span under the `System.Net.Http` scope, carrying the
`qyl.instrumentation.domain` the processor stamped.

## How a library gets instrumented

> Does `AddSource("<name>")` alone deliver spans, without a contrib package and without a
> `DiagnosticListener` adapter?
>
> - **Yes**: the source name goes in `QylTelemetrySources`, `AddQyl()` subscribes it, and
>   `QylNativeSpanProcessor` stamps the qyl attributes onto the library's own spans.
> - **No**: a Roslyn source interceptor in `Qyl.Telemetry.AutoInstrumentation`.

A library that emits only `DiagnosticSource` events counts as No. A library whose `ActivitySource`
stays silent until the consumer opts in takes source + processor; qyl never makes the opt-in call.
`QylTelemetrySources` reads every source name from `QylTelemetryNames`, except
`Experimental.ModelContextProtocol`, which the registry does not declare yet.

### Subscribed native sources

One row per entry of `QylTelemetrySources.NativeSourceRows`: the source name, the instrumentation id
whose toggle gates it, and the `qyl.instrumentation.domain` value the processor stamps. `Azure.*`
matches by prefix; every other row is an ordinal exact match.

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

- Quartz 4 also publishes metrics on its `Quartz` meter; `AddQyl` subscribes it, controlled by
  `OTEL_DOTNET_AUTO_METRICS_QUARTZ_INSTRUMENTATION_ENABLED` (default: the global metrics setting).
- The Elasticsearch client enriches Elastic.Transport's source, so the two share one row and domain.
- CoreWCF's spans are the WCF server side and the registry has no domain value for it
  (`rpc.wcf.client` is the intercepted client), so its row only adds the source.
- Subscribed without a processor row, because the library already writes the conventions:
  `System.Net.Http`, `Experimental.Microsoft.Extensions.AI`, `Experimental.Microsoft.Agents.AI`,
  `Microsoft.Agents.AI.Workflows` and `Experimental.ModelContextProtocol`. The AI sources need the
  consumer's opt-in: `chatClient.AsBuilder().UseOpenTelemetry().Build()` for
  `Microsoft.Extensions.AI` 10.10.0, `agent.AsBuilder().UseOpenTelemetry().Build()` for
  `Microsoft.Agents.AI` 1.24.0 and `WorkflowBuilder.WithOpenTelemetry()` for
  `Microsoft.Agents.AI.Workflows` 1.24.0. `ModelContextProtocol` 2.2.0 and CoreWCF emit without one.
- MCP metrics are not registered: their instruments carry dynamic tool and resource names as
  dimensions, which breaks bounded cardinality.

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
`ActivitySource` at the pinned version. The ADO.NET declaration lists Npgsql, MySqlConnector,
MySql.Data and ODP.NET in `NativeSourceReceivers`: their call sites get no interceptor and no
`QYL1001`, because their own sources cover them.

### The libraries whose telemetry the consumer turns on

The source generator reports `QYL1002` when a compilation uses one of these libraries and never makes
the call: Info for GraphQL.NET and ODP.NET, Warning for the three GenAI libraries.

| Library | The call | Without it |
| --- | --- | --- |
| GraphQL.NET | `IGraphQLBuilder.UseTelemetry()` | the `GraphQL` `ActivitySource` stays silent: no spans at all |
| Oracle ODP.NET | `TracerProviderBuilder.AddOracleDataProviderInstrumentation()` (package `Oracle.ManagedDataAccess.OpenTelemetry`) | the command spans still arrive, carrying only `db.system`, `db.odp.roundtrip.count`, `db.odp.roundtrip.duration` and `db.response.returned_rows`; `db.name`, `db.user`, `db.statement`, `server.address`, `server.port`, `db.odp.connection.id`, `db.odp.sql_id` and exception recording stay off |
| Microsoft.Extensions.AI, Microsoft.Agents.AI, Workflows | `UseOpenTelemetry()` / `WithOpenTelemetry()`, or constructing `OpenTelemetryChatClient` / `OpenTelemetryAgent` directly | the GenAI sources stay silent |

MySql.Data needs no opt-in (`AddConnectorNet()` is only `AddSource("connector-net")`). It always
sends the full command text as `db.statement`, with no option to turn it off; masking it is the
collector's job.

### ASP.NET Core

`Microsoft.AspNetCore.Hosting.HttpRequestIn` is the one HTTP server span of a request; qyl starts
none. The runtime creates it without tags, so `AddQylAspNetCoreInstrumentation()` (called by
`AddQyl()`) registers an `IStartupFilter` middleware that fills, where absent:
`qyl.instrumentation.domain`, `http.request.method`, `url.scheme`, `url.path`, `url.query` under the
redaction control, `http.route` and the `{method} {route}` span name after routing,
`http.response.status_code`, `error.type` on failure, and the configured request and response
headers. A client that disconnects before a response is Kestrel's 499: no `error.type`, status unset.

EF Core (`.EntityFrameworkCore`) and `Microsoft.Data.SqlClient` (`.SqlClient`) use the library's
public `DiagnosticListener` hook; neither declares an `ActivitySource`. No signal has two qyl lanes.

### Toggles

Every row above is gated per signal by its instrumentation id:
`OTEL_DOTNET_AUTO_{TRACES|METRICS|LOGS}_<ID>_INSTRUMENTATION_ENABLED`, with
`OTEL_DOTNET_AUTO_{TRACES|METRICS|LOGS}_INSTRUMENTATION_ENABLED` and
`OTEL_DOTNET_AUTO_INSTRUMENTATION_ENABLED` taking precedence. A disabled id contributes neither an
`AddSource` call nor a processor row.

- Query text is opt-in per provider: `OTEL_DOTNET_AUTO_ENTITYFRAMEWORKCORE_SET_DBSTATEMENT_FOR_TEXT`
  and `OTEL_DOTNET_AUTO_SQLCLIENT_SET_DBSTATEMENT_FOR_TEXT`.
- ASP.NET Core header capture, outbound HTTP header capture and gRPC client metadata capture are
  each opt-in.
- GraphQL documents and ODP.NET statement text belong to the libraries' own options
  (`UseTelemetry(o => o.RecordDocument)`, ODP.NET's `SetDbStatementForText`);
  `OTEL_DOTNET_AUTO_GRAPHQL_SET_DOCUMENT` and `OTEL_DOTNET_AUTO_ORACLEMDA_SET_DBSTATEMENT_FOR_TEXT`
  have no reader here.
- qyl never registers a library's own `Meter`. Export `Npgsql`'s or `NServiceBus.Core`'s instruments
  through `OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES` or `QylTelemetryOptions.AdditionalMeters`.

The full 70-row contract, with every environment control, instrumentation option, evidence level
and authoritative source, is the generated
[resolved contract](contracts/generated/qyl-aot-contract.resolved.yaml).

### Selected outbound HTTP headers

`OTEL_DOTNET_AUTO_TRACES_HTTP_INSTRUMENTATION_CAPTURE_REQUEST_HEADERS` and
`OTEL_DOTNET_AUTO_TRACES_HTTP_INSTRUMENTATION_CAPTURE_RESPONSE_HEADERS` take comma-separated names;
absent, empty and whitespace-only lists capture nothing. Selection is case-insensitive, including
`Mcp-Param-*` names. Keys are `http.request.header.<name>` / `http.response.header.<name>`,
lowercase with punctuation kept; values are string arrays of what `NonValidated` exposes, message
and content headers both.

- The request is captured at the native start callback, before runtime propagation and later
  handlers add headers. The response is captured before the span stops. Transport failures keep the
  request fields and omit the absent response. No body is read or buffered.
- Capture follows the HTTP and global controls, sampling, SDK suppression and provider disposal.
- The payload accessors bind .NET 10 private diagnostic payload shapes (verified as NativeAOT on
  .NET 10.0.12). An incompatible payload logs a bounded warning without values or exception text.

### Selected gRPC client metadata

`OTEL_DOTNET_AUTO_TRACES_GRPCNETCLIENT_INSTRUMENTATION_CAPTURE_REQUEST_METADATA` and
`OTEL_DOTNET_AUTO_TRACES_GRPCNETCLIENT_INSTRUMENTATION_CAPTURE_RESPONSE_METADATA` take
comma-separated names, independent of HTTP capture and of each other. Matching is case-insensitive,
key suffixes are lowercase with punctuation kept, and values are string arrays. Selected `-bin`
entries keep the HTTP transport's base64 strings.

- The existing `Grpc.Net.Client` activity is enriched in its synchronous diagnostic callbacks:
  request metadata from `HttpRequestMessage.Headers` at start, response metadata from the initial
  headers and then the trailers at completion, one array per name, before the span stops.
- Values are what `NonValidated` exposes, duplicates and empty strings included; entry boundaries
  another layer combined are not reconstructed. Response bodies are never read. Cancellation and
  failure capture only the metadata that exists, and HTTP child spans get no metadata.
- gRPC and global controls, sampling, suppression and provider lifetime apply.
- The accessors bind Grpc.Net.Client 2.84.0's private diagnostic shapes (verified as NativeAOT on
  .NET 10.0.12). An incompatible payload logs a bounded, value-free warning; native source,
  operation, kind, status and parentage stay unchanged.

## The qyl attributes

`qyl.instrumentation.domain` is the only qyl-owned attribute the instrumentation writes.
`QylNativeSpanProcessor` writes it onto the spans of the native-source table, and the ASP.NET Core
middleware writes it onto the hosting activity. Applications add their own attributes with
`span.SetTag(key, value)`. Its value set is registry-owned (`QylAttributes.InstrumentationDomainValues`):
`app`, `aspnetcore.server`, `azure.sdk`, `db.client`, `db.efcore`, `db.elasticsearch`, `db.mongodb`,
`db.redis`, `db.sqlclient`, `elastic.transport`, `gen_ai.execute_tool`, `gen_ai.invoke_agent`,
`graphql`, `http.client`, `job.quartz`, `messaging.kafka`, `messaging.masstransit`,
`messaging.nservicebus`, `messaging.process`, `messaging.rabbitmq`, `rpc.grpc` and
`rpc.wcf.client`. The generated starters and `Spans.Work` stamp `app`, `messaging.process`,
`gen_ai.invoke_agent` and `gen_ai.execute_tool`.

`session.id`: the ASP.NET Core middleware stamps the request's W3C `baggage` member `session.id` onto
the server span when it is a valid session id (unreserved URI characters, at most 128, not dots
alone). `QylSessionSpanProcessor` copies `session.id` from the nearest tagged in-process ancestor onto
descendants without one, at span end; remote parents propagate nothing. qyl writes no baggage.
`tests/Qyl.Telemetry.Integration.Tests/Lanes/SessionPropagationTests.cs` verifies this across two
processes.

Scopes: `Qyl.Telemetry.AutoInstrumentation` (`ActivitySource`),
`Qyl.Telemetry.AutoInstrumentation.Database` (`Meter`, `db.client.operation.duration`) and
`Qyl.Telemetry.Application` (application spans). Both sources carry `SchemaUrl.Qyl`
(`https://qyl.at/schemas/<release>`). The generated-code ABI anchor is `QylGeneratedCodeAbi.V28` in
`Qyl.Telemetry.AutoInstrumentation.GeneratedCode`; it tracks the package major, so a generated
interceptor from another major fails to compile.

## Analyzers and generator diagnostics

| Id | Severity | Reported when |
| --- | --- | --- |
| `QYL1001` | Info | A call site names a declared integration's receiver type and method but its signature does not fit the declared shape, typically because the library changed the signature in a new major. No interceptor is emitted for that call, so it produces no qyl telemetry. Update the declaration or pin the library to a version the shape describes. |
| `QYL1002` | Info; Warning for the GenAI rows | The compilation uses a library whose native `ActivitySource` qyl subscribes but never makes the call that library needs before it emits, or before it emits in full — see [the opt-in table](#the-libraries-whose-telemetry-the-consumer-turns-on). The message names the call and what is lost without it. |

`Qyl.Telemetry.AutoInstrumentation` depends on `Qyl.Telemetry.SemanticConventions.Analyzers` with
`PrivateAssets="none"`, so the `QYL0xxx` rules reach every consumer through `Qyl.Telemetry.Hosting`,
among them `QYL0112` (a constant `Spans.Work` name), `QYL0110` (a rethrow inside a starter's scope
calls `RecordFailure`) and `QYL0120` (no duplicate tracer instrumentation next to `AddQyl`). A library
that version-locks to the incubating tier sets `OtelSemConvInstrumentationLibrary=true` (QYL0008).

## Commands

Fallout targets in `eng/build`; `make <target>` wraps `./eng/build.sh <Target>`, and
`./eng/build.sh --help` lists every target and parameter.

```bash
./eng/build.sh Check                           # CI gate: contracts, Release builds, API report, generator snapshots, package layout, consumer behavior incl. NativeAOT
./eng/build.sh CheckFull                       # + NativeAOT publish matrix, thirty demo lanes, LiveCheck, Smoke, WebApiAotDemo, OtlpEvidence
./eng/build.sh Demos --lane quartz http-client # selected demo lanes (all thirty without --lane)
./eng/build.sh AotPublishGate --aot-set warned # clean, warned or all
./eng/build.sh LiveCheck --live-check-skip mongodb
./eng/build.sh Contracts --write-contracts     # regenerate contracts/generated
./eng/build.sh TestbedProbe
./release.sh                                   # pack <Version>, push to nuget.org, tag v<Version>, push the tag
```

- `Check` rewrites `src/*/PublicAPI.txt` and never fails on an API change; it fails unless the top
  `CHANGELOG.md` entry is `<Version>`.
- `Demos`: thirty demo applications under `demos/`, run as native MSTest cases in
  `tests/Qyl.Telemetry.Integration.Tests/Lanes/<Name>Tests.cs`, NativeAOT where the integration
  supports it. Each run owns a `Qyl.Telemetry.Testing` OTLP receiver, passed as `QYL_ENDPOINT`;
  the case compares the application's report with independent expectations and the complete
  capture. Captures and registry reports stay under `artifacts/integration-tests`. Ten lanes need a
  [testbed](#testbed-on-railway) service. Run by hand, a demo exports through `AddQyl()`'s
  discovery to a running `qyl up`.
- The HTTP-header and gRPC-metadata fixtures use `Qyl.Telemetry.Tests.Capture` from the submodule
  `eng/dependencies/Qyl.Telemetry.Tests`; `./eng/build.sh` initializes it, direct `dotnet` commands
  need `git submodule update --init -- eng/dependencies/Qyl.Telemetry.Tests`.
- `OtlpEvidence` decodes a package consumer's export with the official protobuf messages and checks
  path, content type, typed values and `url.full` redaction on the wire.
- `LiveCheck` runs `weaver registry live-check` (the `mise.toml` weaver) on 4317 against nine
  native-source demo lanes (`azure`, `corewcf`, `elastictransport`, `elasticsearch`, `masstransit`,
  `mongodb`, `nservicebus`, `quartz`, `rabbitmq`) with `--fail-on violation` and no allowlist. The
  registry is a checkout of
  [Qyl.Telemetry.SemanticConventions](https://github.com/ANcpLua/Qyl.Telemetry.SemanticConventions)
  at `v` + the `Qyl.Telemetry.SemanticConventions` pin, cloned into `.semconv`; the target passes
  both `--config <registry>/.weaver.toml` and `--advice-policies <registry>/policies/live_check_advice`
  and fails when the checkout's `VersionPrefix` differs from the pin. `RabbitMQ.Client.Subscriber`
  has no consuming demo, so its spans are not judged.
- Collector persistence is tested in qyl.

## Testbed on Railway

The verifiers' databases and brokers run in the Railway project `qyl-testbed` (region
`europe-west4-drams3a`), declared in [`.railway/railway.ts`](.railway/railway.ts) (partial
`autoinstrumentation`), each service behind a public TCP proxy. Services sleep after 5 to 10 minutes
without outbound traffic and wake on the next connection, keeping their filesystem until a
redeploy, so clients retry until the service answers and verifiers create their data idempotently.

| Service | Image | Serves | Limit (vCPU / memory) |
| --- | --- | --- | --- |
| `rabbitmq` | `rabbitmq:4.1-alpine` | masstransit, rabbitmq | 1 / 512 MiB |
| `kafka` | `apache/kafka:4.1.0` (KRaft, SASL/PLAIN) | kafka | 1 / 1 GiB |
| `mongodb` | `mongo:8-noble` | mongodb | 1 / 1 GiB |
| `redis` | `redis:8-alpine` (requirepass) | redis | 1 / 512 MiB |
| `postgres` | `postgres:18-alpine` | npgsql | 1 / 512 MiB |
| `mysql` | `mysql:9` | mysqlconnector, mysqldata | 1 / 1 GiB |
| `oracle` | `gvenzl/oracle-free:23-slim-faststart` | oraclemda | 2 / 3 GiB |
| `sqlserver` | `mcr.microsoft.com/mssql/server:2022-latest` | sqlclient | 2 / 2 GiB |

Connection strings: `~/.config/vitals/qyl-testbed.env` (`chmod 600`, Vitals "qyl testbed connection
strings (Railway qyl-testbed)"), `KEY=value` lines without quotes or `export`. Passwords exist only
as Railway variables (`preserve()`) and in this file.

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

`QYL_TESTBED_WAKE_SECONDS_MAX` (120) is how long a client retries a sleeping service.

```bash
bun install --cwd eng/testbed && bun eng/testbed/probe.ts  # wake all eight, one line per service; --verbose
railway link --project qyl-testbed                         # once per checkout
eng/testbed/provision.sh                                   # create missing services and secrets, apply, write the env file
eng/testbed/provision.sh env                               # rewrite the env file only
railway config plan --detailed-exit-code                   # 0 when Railway matches .railway/railway.ts
```

## Versions

`<Version>` in `Directory.Build.props` versions all six packages; the package major is the
compile-time ABI (`QylGeneratedCodeAbi.V<X>`). The `Qyl.Telemetry.SemanticConventions`
`PackageVersion` in `Directory.Packages.props` names the semantic-convention release: the code
compiles against its constants, `LiveCheck` derives its registry tag from it, and `Contracts` fails
when a tracked file hard-codes another `SEMCONV_REF` or registry tag. A package exposes that release
as the nuspec dependency's lower bound and as
`[assembly: AssemblyMetadata("QylSemanticConventionsVersion", "<version>")]` on
`Qyl.Telemetry.AutoInstrumentation.dll`, which `PackageLayout` checks against the pin.

## Limitations

- Only source-visible call sites can be intercepted. Calls hidden in compiled dependencies,
  reflection or dynamic dispatch need a public runtime hook or remain unsupported.
- Some integrations are managed-only because the instrumented library requires runtime code
  generation. There is no parity claim with the CLR-profiler OpenTelemetry .NET automatic
  instrumentor, and no claim that every third-party library publishes warning-free under NativeAOT.
- Query text and other sensitive or high-cardinality values remain opt-in or redacted according to
  the package options and the upstream OpenTelemetry controls.
- Generator snapshots prove emitted source shape; protocol interoperability needs a real OTLP
  receiver and structural decoding of the official protobuf messages.
- MassTransit 9 is commercially licensed and is neither referenced nor redistributed here; the demo
  pin is the Apache-2.0 range `[8.5.11,9.0.0)`.

## License

Apache-2.0
