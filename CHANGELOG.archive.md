# Changelog archive

Entries before 25.0.0; later ones are in [CHANGELOG.md](CHANGELOG.md).

## [24.1.1] - 2026-10-14

### Security

- `Qyl.Telemetry.AutoInstrumentation.SqlClient` depends on `System.Security.Cryptography.Pkcs`
  10.0.13. `Microsoft.Data.SqlClient` 7.1.1, the newest release, brings 9.0.18, which the
  advisories of 2026-10-13 (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7) cover; the floor reaches
  consumers through the package.

### Changed

- The build host and the SqlClient, Oracle and WCF demos reference the patched
  `System.Security.Cryptography.Pkcs` and `.Xml` 10.0.13 (Fallout.Common brought 10.0.10 of both;
  GHSA-7hqp-7c5m-4vq6, GHSA-c399-9853-6jmj, GHSA-r955-8q5j-3h83); NuGet audit failed those
  restores.
- The demo lanes' local collector is the `qyl` tool 12.2.0.

## [24.1.0] - 2026-10-14

### Added

- `Qyl.Telemetry.AutoInstrumentation.dll` carries
  `[assembly: AssemblyMetadata("QylSemanticConventionsVersion", "9.9.2")]`, the
  `Qyl.Telemetry.SemanticConventions` release it compiled against, taken at build time from the
  resolved reference rather than typed again. The nuspec dependency on
  `Qyl.Telemetry.SemanticConventions` names the same version as its lower bound, and
  `./eng/build.sh PackageLayout` checks both against the pin.

### Changed

- `Directory.Packages.props` is the one place that names the semantic-convention release. The live
  check clones the registry at `v` + the `Qyl.Telemetry.SemanticConventions` pin;
  `tools/live-check.env` and its hand-kept `SEMCONV_REF` are deleted, and `./eng/build.sh Contracts`
  fails when a tracked file (the CHANGELOG's history aside) hard-codes a `SEMCONV_REF` or a registry
  tag that names another release.
- The `release.sh` dry run retries a failed key probe twice before refusing, so a dropped
  connection to nuget.org no longer reads as a rejected key.

Older entries: [CHANGELOG.archive.md](CHANGELOG.archive.md).

## [24.0.2] - 2026-10-14

No change to the packages' own source or public API: they are rebuilt on the dependency pins
below, and the build, the demos and the release tooling around them change.

### Changed

- **Build, test and pack go through Fallout targets in C#** (`eng/build`, `./eng/build.sh <Target>`,
  `./eng/build.sh --help`). They replace the 55 Python scripts under `tools/`, their
  `requirements.txt`, `tools/smoketest.sh`, the Makefile lanes and the mise tasks: one toolchain.
  `./eng/build.sh Check` is the gate before a push or a release (it was `mise run check`) and
  `./eng/build.sh CheckFull` runs everything (it was `mise run check-full`); `make <target>` and the
  two mise tasks are thin wrappers over them. The targets: `Build`, `Pack`, `Contracts`,
  `ApiBaseline`, `GeneratorSnapshots`, `PackageLayout`, `ConsumerBehavior`, `AotPublishGate`,
  `Demos`, `LiveCheck`, `Smoke`, `WebApiAotDemo`, `OtlpEvidence`, `Check`, `CheckFull` and
  `TestbedProbe`.
- **The demos are asserted through a local qyl collector.** Every demo exports OTLP to the collector
  the `qyl` dotnet tool ships (pinned in `.config/dotnet-tools.json`, started once per build run on
  free loopback ports) through `QYL_ENDPOINT`, and its lane asserts, besides the demo's own report,
  that every span, measurement series and log record the report names arrived with the reported
  name, kind, status and attribute values, read through the collector API typed by
  `Qyl.Api.Contracts`. Every span a demo report lists carries its `TraceId` and `SpanId`. The demos
  that wired no exporter (ADO.NET, SQLite, EF Core, Kafka, Redis, SqlClient, WCF client and the two
  metrics demos) export through `demos/Shared/QylDemoOtlp.cs`; the `AddQyl()` demos take its
  discovery path instead of a hard-coded endpoint.
- **The ten broker and database lanes use the Railway testbed instead of Docker.** They read
  `~/.config/vitals/qyl-testbed.env` (`QYL_TESTBED_ENV` overrides the path), wake a sleeping
  service with a protocol-level probe and a visible countdown for up to
  `QYL_TESTBED_WAKE_SECONDS_MAX`, and fail naming the service. The SqlClient lane runs on any
  machine now that SQL Server runs x64 on Railway; the Kafka demo speaks SASL/PLAIN when
  `QYL_KAFKA_SASL_USERNAME` and `QYL_KAFKA_SASL_PASSWORD` are set; the MongoDB demo empties its
  collection first, because a woken testbed keeps its data.
- The contracts cite the C# gates as evidence (`eng/build/Demos/Lanes/<Name>Lane.cs`,
  `eng/build/ConsumerBehavior/*.cs`), and the generated contract artifacts are rewritten by
  `./eng/build.sh Contracts --write-contracts`. Consumer fixtures live in `eng/fixtures`.
- `release.sh` packs through `./eng/build.sh Pack` into `artifacts/release`. Its dry run reports
  the branch, origin, tag and already-indexed checks as warnings instead of refusing, so it proves
  the pack from any branch, and it proves the NuGet key for every package id through NuGet's
  package-verification handshake instead of pushing an already-published package with
  `--skip-duplicate`: a dry run publishes nothing.
- Compile against `Qyl.Telemetry.SemanticConventions` 9.9.2 (from 9.9.0) and run the live check
  against the `v9.9.2` registry. 9.9.1 declared `qyl.gen_ai.tool.call.arguments_hash`, which qyl
  does not emit; 9.9.2 changed only how that repository builds and tests. The source generator
  embeds `ANcpLua.Roslyn.Utilities.Sources` 2.2.48 (from 2.2.47), and the build types the collector
  client with `Qyl.Api.Contracts` 12.1.2 (from 12.1.1, the same contract revision).
- Version sync requires the top CHANGELOG entry to be `## [<Version>] - YYYY-MM-DD` and rejects
  an `[Unreleased]` entry: a change is recorded under the version that ships it, bumping
  `<Version>` first when that version is already released.

### Fixed

- `bun eng/testbed/probe.ts` no longer prints a `TimeoutNegativeWarning`: kafkajs 2.2.4 schedules
  its request-queue check `throttledUntil - Date.now()` ms ahead, which is `-1 - Date.now()` while
  the queue is unthrottled. `eng/testbed/patches/kafkajs@2.2.4.patch` clamps the delay at 0.
- The ILogger demo exported no log record over OTLP: its first export ran inside the host's
  disposal against an already-disposed container. The gRPC client demo never flushed its batch
  exporter. Both were invisible while the demos asserted only their in-memory exporters.

## [24.0.1] - 2026-10-12

### Changed

- Compile against `Qyl.Telemetry.SemanticConventions` 9.9.0 and validate emitted telemetry
  against the matching registry. Its C# and TypeScript bindings are generated from the same
  resolved qyl registry. qyl continues to own its instrumentation; the upstream .NET automatic
  instrumentation 1.17.0 contract remains a comparison baseline, and the OpenTelemetry SDK,
  hosting integration and OTLP exporter remain at 1.19.1.
- Bind the release tag to the packed commit and reject concurrent tracked edits before publishing.
- Run the full local verification gate without building the solution twice. Release and
  dependency-update documentation now describes manual publishing and deployment accurately.

## [24.0.0] - 2026-10-11
A breaking release: three public names in `Qyl.Telemetry.Hosting` change, and
none of the old names remains as an alias.

### Changed (breaking)

- **`QylSdkOptions` is `QylTelemetryOptions`.** The package has not been called `Qyl.Sdk` since 9.0.0; the
  options type kept the retired name. Every property keeps its name, type and default, apart from
  the one below.
- **`QylSdkHostApplicationBuilderExtensions` is `QylHostApplicationBuilderExtensions`.** The method
  is still `AddQyl(this IHostApplicationBuilder, Action<QylTelemetryOptions>?)`. A call written as
  `builder.AddQyl(...)` compiles unchanged. Only code that names the class, such as a static call
  `QylSdkHostApplicationBuilderExtensions.AddQyl(builder)` or a `cref`, has to change.
- **`EnableSessionPropagation` is `EnableSessionTagInheritance`.** The option copies the
  `session.id` span tag from the nearest in-process ancestor onto descendants that lack one. It
  never propagated anything across a process: no baggage, no header. The old name promised
  cross-process propagation, and the new name says what the option does. Its default (`true`) and
  its behaviour are unchanged.
- The generated-code ABI anchor is `QylGeneratedCodeAbi.V24`, because the major version moved.

Migration: replace `QylSdkOptions` with `QylTelemetryOptions`, `QylSdkHostApplicationBuilderExtensions`
with `QylHostApplicationBuilderExtensions`, and `.EnableSessionPropagation` with
`.EnableSessionTagInheritance`. A type of your own named `QylTelemetryOptions` in a namespace under `Qyl`
is now shadowed by `Qyl.QylTelemetryOptions` in code that imports it with a `using` directive. Rename it,
or qualify it. Telemetry does not change. Attribute names, metric names and units, event names,
scope names and the `AddQyl()` method name are the same as in 23.1.0.

## [23.1.0] - 2026-10-11

### Added

- Subscribe to Quartz 4's native `Quartz` meter by default. The existing metrics configuration
  now binds `QUARTZ`, including per-instrumentation overrides. Quartz owns the instruments and
  dimensions; qyl forwards them. The real Quartz demo verifies successful and failed scheduler
  executions, duration histogram counts in seconds, native result/error dimensions, disabling
  metrics globally or for Quartz, and explicitly re-enabling Quartz over a global disable.
  Quartz's NativeAOT evidence remains managed-only.

### Changed

- Refresh the upstream comparison contract to .NET AutoInstrumentation `v1.17.0` (released
  2026-09-22), adding its Quartz metrics promise and current Quartz/Redis version boundaries.
  qyl remains an independent implementation. The OpenTelemetry SDK stays at `1.19.1`;
  SemanticConventions moves to `9.8.0`, which defines all 11 native Quartz metrics.
- Refresh the qyl-native contract's verified Microsoft.Extensions.AI `10.10.0` and
  Microsoft.Agents.AI/Workflows `1.24.0` paths to match the existing package pins.

## [23.0.0] - 2026-10-10

### Changed

- **Every library pin moves to the newest stable release, and the registry describes those
  releases.** Microsoft.Data.SqlClient 7.1.1, MongoDB.Driver 3.12.0, Quartz 4.4.0,
  StackExchange.Redis 3.4.0, Confluent.Kafka 2.16.0, Grpc.Net.Client and Grpc.Core.Api 2.84.0,
  Elastic.Clients.Elasticsearch 9.5.3 with Elastic.Transport 1.1.0 (the transport the client
  declares; 8.0.1 is a different line), Azure.Storage.Blobs 12.30.1 with Azure.Core pinned
  directly at 1.63.0, Oracle.ManagedDataAccess.Core 23.26.301, MassTransit.RabbitMQ at an 8.5.11
  floor, Roslynator 5.0.1 and ANcpLua.Roslyn.Utilities.Sources 2.2.47. `Qyl.Telemetry.SemanticConventions`
  9.7.0 re-read the vendor models for every one of these at the new tags, Quartz 4.4.0 among them
  with three new keys, so the constants this instrumentation compiles against and the keys the
  collector passes through name the releases actually running. The live-check and publish
  workflows check out the matching `v9.7.0` registry. MassTransit stays on the Apache-2.0 8.x
  line by the maintainer's 2026-09-04 decision; it is the one pin not at its newest major.
- **The AOT publish gate's approvals follow the pins.** The approved trim warnings name
  Microsoft.Data.SqlClient 7.1.1 with the System.Configuration.ConfigurationManager 9.0.18 it now
  resolves, Oracle.ManagedDataAccess.Core 23.26.301 and MassTransit 8.5.11; the WebApi AOT demo's
  generated consumer pins SqlClient 7.1.1. Measured locally: 16 demos warning-clean, 8 at exactly
  their approved diagnostics, no promotion.
- The generated-code ABI anchor is `QylGeneratedCodeAbi.V23`. A major version because every
  instrumented library's line moves at once; no public API of this package changed.

## [22.0.0] - 2026-10-10

### Changed

- **`Qyl.Telemetry.SemanticConventions` 9.6.0, Microsoft.Agents.AI and .Workflows 1.24.0,
  Microsoft.Extensions.AI 10.10.0, OpenTelemetry 1.19.1.** The registry's genai pin moved to
  upstream's 2026-10-09 head, where `gen_ai.client.token.usage` and `gen_ai.token.type` no longer
  exist and the inference signals live under `gen_ai.client.inference.*`. The libraries this
  package instruments have not followed: Agent Framework 1.24.0 still delegates every span and
  instrument to Microsoft.Extensions.AI's `OpenTelemetryChatClient`, which records that histogram
  with that dimension, and the GenAI demo's fourteen measurements are byte-for-byte what 1.20.0
  and 10.9.0 produced. 9.6.0 therefore carries three vendor models read at exactly these
  versions, `registry/vendor/microsoft-extensions-ai.yaml`, `microsoft-agents-ai.yaml` and
  `microsoft-agents-ai-workflows.yaml`, so the constants this demo compiles against
  (`GenAiAttributes.TokenType`, `TokenTypeValues.Input`, `.Output`) are vendor rows now, the
  collector passes the keys through instead of dropping them, and the sixteen `workflow.*`,
  `executor.*`, `message.*` and `edge_group.*` keys the workflow runtime writes reach the
  collector for the first time. The live-check and publish workflows check out the matching
  `v9.6.0` registry.
- **The three GenAI source names are generated constants.** `QylTelemetrySources` spelled
  `Experimental.Microsoft.Extensions.AI`, `Experimental.Microsoft.Agents.AI` and
  `Microsoft.Agents.AI.Workflows` as literals because the registry did not know them; they are
  `QylTelemetryNames.VendorActivitySources` members now, as every other subscribed source already
  was, so QYL0200 accepts them and a renamed source upstream is a compile error here rather than
  a silent miss.
- Microsoft.Extensions.* 10.0.12 and EntityFrameworkCore 10.0.12, the floor Microsoft.Extensions.AI
  10.10.0 declares. A major version because the dependency line of every package moves and the
  instrumented GenAI stack is a different release. The generated-code ABI anchor moves with the
  major, as it always does: `QylGeneratedCodeAbi.V22` replaces `V21`, so an interceptor generated
  by a 21.x generator fails to compile against this runtime instead of binding to it. The AOT
  publish gate's approved EF Core trim warnings name 10.0.12 now, because an approval pins the
  exact package the warning came from, and the consumer projects the verifiers under `tools/`
  generate pin the same EF Core, Sqlite, Hosting and OpenTelemetry versions as
  `Directory.Packages.props`.

## [21.1.0] - 2026-09-11

### Changed

- **`Qyl.Telemetry.SemanticConventions` 9.4.0.** The dependency line moves from 9.3.0; the
  registry's schema URL is now versioned with the package and resolves at
  `https://qyl.at/schemas/9.4.0`, so the `QylSchemaUrl` constant this instrumentation carries
  through `AttributeMapping` names a real document. No attribute, metric, span or event changed
  upstream. The live-check and publish workflows check out the matching `v9.4.0` registry.
- The README names no version of this package or of its dependency line; `Directory.Packages.props`
  is the one place that pins, and nuget.org is the one place that publishes.

## [21.0.2] - 2026-09-11

### Changed

- **Each package ships its own README.** Every `Qyl.Telemetry.*` package carried the whole
  repository README, 450 lines of registry, interception, demo and release detail, as its
  nuget.org readme. Each of the six packable projects now has a consumer README beside its
  csproj: what the package is, how to add it, the one call or the controls that matter, and
  absolute links into the repository for the rest. `Directory.Build.props` packs
  `$(MSBuildProjectDirectory)/README.md`; the repository README stays in the repository. No
  code change.

## [21.0.1] - 2026-09-09

21.0.0 is a tag without packages: its publish run went red in the NativeAOT gate before the
publish job, so nothing reached the feed. The content of 21.0.0 ships here, with the gate defect
that stopped it.

### Fixed

- **The NativeAOT publish gate wrote the .NET runtime version down as a literal, and .NET moved.**
  Each tolerated third-party diagnostic is pinned by id, assembly, package and resolved version.
  For five of them the package is a runtime pack -- `microsoft.aspnetcore.app.runtime.{rid}` and
  `microsoft.netcore.app.runtime.nativeaot.{rid}` -- whose version ships with the SDK, moves with
  every servicing release and is recorded nowhere in this repository. The table said `10.0.11`.
  On 2026-09-09 the Linux runners resolved `10.0.12` and the gate reported `package drift` on a
  tree nobody had touched, failing `Qyl.RealAspNetCoreMetricsDemo` and `Qyl.RealWcfClientDemo`.
  A literal could not have been right for both: this machine still has `10.0.11`.

  Those five entries now carry a placeholder filled from the runtime the machine actually has,
  measured with `dotnet --list-runtimes` and restricted to the major band `global.json` targets.
  The pin stays exact -- a publish that resolves a runtime pack other than the runtime it runs on
  is still drift and still fails -- it just is no longer frozen. Every other version in the table
  is a real dependency pinned in `Directory.Packages.props` and stays a literal, because a change
  there is a deliberate act.

  Proven both ways on macOS: green for both demos with the measurement, and red for
  `Qyl.RealAspNetCoreMetricsDemo` with `package drift ... @10.0.11 != ...@10.0.99` when the
  measurement is forced to a wrong value, then restored byte-identically.

## [21.0.0] - 2026-09-09

### Fixed

- **A client that disconnects no longer exports a 500 that was never sent.** When the handler is
  still waiting on `RequestAborted` and the client goes away, the cancellation unwinds through the
  pipeline and the server span reported `http.response.status_code=500`,
  `error.type=System.Threading.Tasks.TaskCanceledException` and status `Error`. Kestrel's own
  rule for that case is the opposite: an `OperationCanceledException` or `IOException` while the
  connection is aborted is not an application error, nothing is sent, the request is logged as
  499 and no unhandled-exception event fires. Measured on .NET 10: at the moment qyl's middleware
  sees the exception the response still reads 200, and Kestrel writes its 499 only after the
  pipeline has unwound, so the middleware was deciding before the framework had. The span now
  follows the same rule at the same point — 499, no `error.type`, status unset — which is what
  `OpenTelemetry.Instrumentation.AspNetCore` exports for the identical request. Datadog and New
  Relic hard-code the 500; Sentry and Elastic leave the default 200; Dynatrace leaves the code empty
  and documents an empty code as a client abort. None of them exports what the server did.

  The real ASP.NET Core demo drives the disconnect as its fourth request and the gate asserts the
  499 span, the absence of `error.type` and the unset status. Proven red without the fix, for that
  reason and no other, and green with it.

  Found by the 14.1.0 code review and left open through six majors; the other four findings of that
  review were closed within the hour.

### Changed

- **`docs/` is gone.** It held no documentation: three contract inputs, three generated
  artifacts and a coverage matrix, all read or written by `tools/`. They now live under
  `contracts/`, where an input belongs. Every path constant, the schema's `const` values,
  the matrix header and the README link follow. Nothing was deleted -- `--check` is green
  and the regeneration is idempotent.
- **`otel-dotnet-auto-60.upstream.yaml` is now `otel-dotnet-auto.upstream.yaml`.** The
  contract has held 63 rows since 19.0.0; a filename asserting 60 is the same rot as a
  frozen constant, one directory further out.
- **Five empty directories removed**, four of them tracked by nothing and invisible to
  `git ls-files` because git does not carry empty directories: `docs/rfc`, `docs/schema`
  and `docs/upstream` (all dated 2026-07-13), the shells `demos/Qyl.LiveInstrumentationDemo`
  and `demos/Qyl.RealTcgPublishingDemo` referenced by no solution, workflow or tool, and
  `.agents/skills/qyl-selfhosted-ci`. Searching for files rather than for directories had
  reported them absent.

## [20.0.0] - 2026-09-08

### Fixed

- **The vocabulary check read a repository it does not own, and 18.0.0 and 19.0.0 never
  published because of it.** 18.0.0 widened the check from `src/` to the whole tree so it
  could see writes in demos. CI clones the semantic-conventions registry into `.semconv/`
  at the repository root as `QYL_SEMCONV_REGISTRY`, so the walk read that registry's own
  analyzer test cases and reported them as qyl writing key literals. Both tags were cut
  before it showed, because `.semconv/` does not exist on a developer machine — the failure
  needed CI to exist at all.

  The check now asks git which C# files this repository tracks, which is what "ours" means.
  An exclusion list would have needed a new entry for every future checkout. Proven both
  ways: the same `SetTag` with a key literal is ignored in an untracked `.semconv/` file and
  still fails the check in a tracked one, and an empty file list is itself a failure so the
  invariant cannot pass by checking nothing.

  17.0.0 remains the published version; 18.0.0 and 19.0.0 exist as tags with no packages.
  Their content is in this release.

## [19.0.0] - 2026-09-08

The upstream contract had been pinned to two commits from June, and both had moved. Pulling the
pins forward showed the frozen row count for what it was: `generate-contract-artifacts.py` refused
to load any upstream contract that was not exactly 60 rows, so an upstream that grew could not be
recorded without first editing the tool. Upstream grew by three rows.

### Changed

- **The upstream contract is pinned to today's sources.**
  `docs/contracts/otel-dotnet-auto-60.upstream.yaml` now pins
  `open-telemetry/opentelemetry-dotnet-instrumentation` `docs/config.md` at
  `b9909b7505a9b857b1b3eee1d01f5022abf3010c` (2026-09-02, "Npgsql trace context propagation")
  instead of `611b815a62a66b5ece634337328757444fb9c2e9`, and
  `open-telemetry/opentelemetry.io` `content/en/docs/zero-code/dotnet/instrumentations.md` at
  `69edfe0c981cb3559854006de46065bde48b07dc` (2026-07-09, "Update docs after OTel .NET Auto 1.16.0
  release") instead of `db8337edbbac824aebbb330acea18a7042b38806`. Every per-row line anchor was
  remapped by locating the pinned line's text in the new file, not by guessing an offset.

- **`signals.traces.NPGSQL` and `signals.traces.SQLCLIENT` are no longer source-only.** Upstream
  now documents both as `source & bytecode`, where the bytecode half exists solely for trace-context
  propagation into the server -- `OTEL_DOTNET_AUTO_NPGSQL_CONTEXT_PROPAGATION` writes the traceparent
  through PostgreSQL's `application_name`, and
  `OTEL_DOTNET_EXPERIMENTAL_SQLCLIENT_ENABLE_TRACE_CONTEXT_PROPAGATION` does the .NET Framework
  equivalent for SQL Server. Both rows carry the new operator and the reason.

- **The contrib documentation pins moved with upstream's 1.18.\* bump.** `Instrumentation.AspNet`,
  `AspNetCore`, `Http`, `Process`, `Runtime` and the `SqlClient` release tag are now the versions
  upstream's config actually links, not the 1.15.\* ones from June.

### Added

- **Three upstream instrumentation options that did not exist at the old pin.**
  `OTEL_DOTNET_AUTO_NPGSQL_CONTEXT_PROPAGATION` (`false`),
  `OTEL_DOTNET_AUTO_ORACLEMDA_DATABASE_OPENTELEMETRY_TRACING` (`true`) and
  `OTEL_DOTNET_EXPERIMENTAL_SQLCLIENT_ENABLE_TRACE_CONTEXT_PROPAGATION` (`false`) are contract items
  061-063. The ownership overlay records what qyl actually does with them, which is nothing: the
  first two are `not_implemented` / `research_required` because the mechanism upstream uses is a
  rewrite of connector internals qyl never sees -- it consumes Npgsql's and ODP.NET's own
  `ActivitySource`s through `QylNativeSpanProcessor` and has no handle on the command lifecycle. The
  third is `unsupported_nativeaot`: upstream scopes it to .NET Framework and implements it by
  bytecode rewriting, and this package family has neither.

### Fixed

- **The contract's size was frozen in the tool instead of read from the contract.**
  `verify_source_contract_sequences` hard-failed on any upstream contract that was not exactly 60
  items with indexes 1..60, `verify_contract_model` on any resolved contract that was not exactly 66,
  and the emitted JSON schema pinned `minItems`/`maxItems`/`index` to 66. Those numbers were
  bookkeeping of what upstream published in June, not a qyl invariant, and freezing them made the
  only honest response to an upstream refresh -- growing the contract -- impossible. They now derive
  from `UPSTREAM_CONTRACT_ITEM_COUNT` and `QYL_NATIVE_CONTRACT_ITEM_COUNT`, so contiguity, the
  `contract_item_id` sequence and the qyl-native scope are still enforced exactly as before, against
  a size the contract sets. The resolved contract is 69 items (63 upstream + 6 qyl-native); the
  qyl-native rows moved from indexes 61-66 to 64-69.

- **`docs/.DS_Store` was tracked.** Deleted.

## [18.0.0] - 2026-09-08

One vocabulary rule guarded `src/` and nothing else, and the two things that fell outside it were
both wrong in opposite directions: a demo wrote `session.id` as a literal and nobody noticed, while
widening the same rule to the whole repository would have outlawed the assertions that make the
demos worth running. A demo that checks what qyl emitted has to name the key literally — taking the
constant would compare it against itself and prove nothing. So the rule now distinguishes writing a
key from reading one, instead of distinguishing `src/` from everything else.

### Fixed

- **`demos/Qyl.RealAspNetCoreDemo` wrote `session.id` as a string literal.** The stand-in for
  `Qyl.Api`'s session-baggage filter stamped the key with `activity.SetTag("session.id", ...)`, and
  the assertion side held its own copy in a `private const string`. Both now come from
  `SessionAttributes.Id` — the same generated constant `QylSessionSpanProcessor` writes — as does
  the `session.id=` baggage-member prefix the filter matches on. The file already reached for
  `HttpAttributes` seven times; the one place that actually wrote a key was the place that did not.
  A registry rename now reaches the demo, and the demo's assertion cannot pass against a key the
  runtime no longer writes.

- **The vocabulary gate falsely flagged a gRPC service name.** `"qyl.LiveProbe"` in
  `demos/Qyl.RealGrpcClientDemo` is a wire identifier, not a telemetry attribute key, and a prefix
  match on `"qyl.` cannot tell the two apart. The pattern now requires attribute-key form —
  lowercase, dot-separated segments — so `LiveProbeClient.cs`, which contains that service name and
  no attribute key at all, is no longer a candidate for the rule in the first place.

### Changed

- **`verify_qyl_vocabulary_literals` splits into two strengths instead of one scope.** Under `src/`
  the key literal stays forbidden outright: an emitting source reads its own vocabulary through the
  constants too. Outside `src/` only a WRITE fails — `SetTag`/`AddTag`/`SetBaggage`/`AddBaggage`
  with the literal as first argument, and a `const string` whose value is a bare key, since a named
  constant is the same write one indirection out. Reads stay legal: `TryGetValue`, `HasTag`,
  `StartsWith`, comparisons and expectation lists. The rule was falsified in six directions before
  it was trusted — a literal write and a literal const of each key in a demo turn it red, a literal
  READ in `src/` turns it red, and the gRPC service name in `src/` does not.

- `QylGeneratedCodeAbi.V17` is renamed to `V18`. The constant is the generated-code ABI anchor and
  moves with the package major, so code generated against 17.0.0 is rejected at compile time rather
  than silently mixed with an 18.0.0 runtime.

## [17.0.0] - 2026-09-07

The repository had a verifier for every foreign technology it instruments and none for its own.
`session.id` is the only thing qyl invents — `QylSessionSpanProcessor` copies it down a trace and
`AddQyl` never puts it on the wire — and how far it travels was argued out from scratch in session
after session because nothing wrote it down. It is written down now, as a gate.

### Added

- **`demos/Qyl.RealSessionPropagationDemo` and `tools/verify-real-session-propagation-demo.py`: the
  permanent gate for qyl's own session behaviour.** One executable with a `--role` argument, started
  as TWO REAL OPERATING-SYSTEM PROCESSES by the verifier — never by the demo, which starts no
  process at all. Three assertions, on concrete values rather than on presence:
  - an in-process descendant of a span tagged `session.id` inherits that exact value;
  - a descendant that already carries its own `session.id` keeps it, and the ancestor does not win;
  - across a real HTTP boundary into the second process the ASP.NET Core server span carries **no**
    `session.id` at all — although `traceparent` arrived, both processes are on one trace, the
    server span parents to the upstream client span, and the session itself reached that span in
    baggage because the application put it there. qyl still writes no tag from it.

  Two hosts in one process would leave `Activity.Parent` linked across the "boundary" and the third
  assertion would pass while being false, which is the measurement error this project has already
  made twice. So the two roles report separately, out of separate SDK pipelines and separate
  in-memory exporters, and each report has to name the process id the verifier actually launched.
  The downstream role additionally proves its own propagation works in the same run, on a locally
  rooted pair of spans: without that control a downstream with no processor at all would satisfy
  the third assertion by doing nothing.

  Every wait is bounded, both processes are killed in a `finally`, and a run starts exactly two
  processes. The gate was falsified three ways before it was trusted — stamping the tag from
  baggage, dropping the "already has a session" guard, and copying nothing at all each turn it red
  on the property they break.

### Changed

- `QylGeneratedCodeAbi.V16` is renamed to `V17`. The constant is the generated-code ABI anchor and
  moves with the package major, so code generated against 16.0.0 is rejected at compile time rather
  than silently mixed with a 17.0.0 runtime.

## [16.0.0] - 2026-09-07

### Changed

- **Breaking.** `AddQyl` now throws `InvalidOperationException` when it is called a second time on
  the same builder *with* options. 15.0.0 made the call idempotent, and a repeat call's options were
  discarded in silence — an application that set a service name or a collector endpoint on its second
  call ran with neither and had no way to notice, because nothing failed and nothing was logged.
  A repeat call without options is still ignored: that is the `AddQylApi` composition and it is
  correct. Configure qyl once, either through `AddQylApi` or by calling `AddQyl` with options before
  it.
- `QylGeneratedCodeAbi.V15` is renamed to `V16`. The constant is the generated-code ABI anchor and
  moves on every breaking change, so code generated against 15.0.0 is rejected at compile time
  rather than silently mixed with a 16.0.0 runtime.

### Internal

- The publish gate's floor runs as four shards per operating system rather than one job carrying all
  nineteen stages, and no longer repeats the NativeAOT publish gate that `qyl-aot-publish-gate`
  already runs on its own matrix.
- The `release` job takes an explicit `RELEASE_TOKEN`. `github.token` returned HTTP 403 on
  `gh release create` for this repository even with `contents: write` declared and the repository
  default set to write, so v15.0.0's release was created by hand.

## [15.0.0] - 2026-09-07

The mapping rule 14.1.0 applied to ASP.NET Core, applied to everything else: **a library with its
own `ActivitySource` gets `AddSource` plus the qyl domain stamp, and the qyl span for it is
deleted.** Six integrations produced two spans for one operation; each now produces one. The whole
release is output changes, so every one of them is named below as old source and span name to new.

### Changed

- **HttpClient: the framework's span is the client span.** `QylInterceptedHttpClient` (the
  forwarding interceptor) and `HttpClientDiagnosticListener` are both deleted, and `System.Net.Http`
  is an ordinary row of the native-source table. Source `Qyl.Telemetry.AutoInstrumentation` span
  `{method}` -> source `System.Net.Http` span `{method}`. The BCL writes `http.request.method`,
  `server.address`, `server.port`, `url.full`, `http.response.status_code`,
  `network.protocol.version` and `error.type` itself; `QylNativeSpanProcessor` adds
  `qyl.instrumentation.domain` = `http.client` and nothing else. Two measured losses, both
  intentional:
  - **`url.full` is redacted the BCL's way, not qyl's.** The whole query becomes `?*` where qyl
    wrote `key=Redacted` per value. And the control changed hands:
    `OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION` now flips the runtime's own
    `System.Net.Http.DisableUriRedaction` switch. The query in `url.full` is redacted only while
    that switch is unset, so a consumer who flips it for their own debugging turns the raw query
    back on in exported spans — a reach the old qyl-owned control did not have.
  - **HttpClient request and response header capture is gone.**
    `OTEL_DOTNET_AUTO_TRACES_HTTP_INSTRUMENTATION_CAPTURE_REQUEST_HEADERS` and
    `..._RESPONSE_HEADERS` have no producer: the span is the BCL's and a processor has no
    `HttpRequestMessage` to read. Both are `not_implemented` in the contract.
- **`QylSignalOwnership` is deleted.** HttpClient was the one signal two qyl lanes could both
  produce, and the arbitration existed only for it. With the interceptor and the listener gone there
  is no second lane to arbitrate against, anywhere.
- **gRPC client: `Grpc.Net.Client`'s own source, and two spans that are two operations.**
  `GrpcClientDiagnosticListener` and `GrpcClientPayloadReader` are deleted. Source
  `Qyl.Telemetry.AutoInstrumentation` span `{method}` -> source `Grpc.Net.Client` span
  `Grpc.Net.Client.GrpcOut`, with the `System.Net.Http` span as its **child** — an RPC and the HTTP
  request that carries it are two operations, not a duplicate. The loss is large and measured:
  grpc-dotnet 2.83.0 emits **no** RPC semantic conventions at all. `rpc.system.name`, `rpc.method`,
  `rpc.method_original`, `rpc.response.status_code`, `server.address`, `server.port`,
  `network.peer.*` and `error.type` are gone; the native span carries exactly two vendor keys,
  `grpc.method` (the full `/service/method` path) and `grpc.status_code` (a decimal **string**), and
  a failed call is distinguishable from a successful one only by that value — `Activity.Status`
  stays `Unset`. Captured request and response metadata go with the listener:
  `OTEL_DOTNET_AUTO_TRACES_GRPCNETCLIENT_INSTRUMENTATION_CAPTURE_{REQUEST,RESPONSE}_METADATA` are
  `not_implemented`.
- **The four database providers leave the ADO.NET interceptor.** Npgsql, MySqlConnector, MySql.Data
  and Oracle ODP.NET are named in the ADO.NET declaration's `NativeSourceReceivers`, so a call site
  on one of those receivers emits no interceptor — and reports no `QYL1001`, because it is not a
  shape mismatch but a lane that belongs to the library. Source
  `Qyl.Telemetry.AutoInstrumentation` span `{db.query.summary}`, carrying `db.system.name`,
  `db.operation.name`, `db.query.summary` and `error.type`, is replaced per provider:
  - **Npgsql** -> source `Npgsql`, one span per command named `postgresql` (the span name no longer
    summarises the statement), plus a `CONNECT {database}` span per *physical* connection open.
    `error.type` and `db.response.status_code` are the **SQLSTATE** (`42703`), not the CLR exception
    type. Gained: `db.namespace`, `server.address`, `server.port`, `db.npgsql.data_source`,
    `db.npgsql.connection_id`, a `received-first-response` event and an `exception` event.
    **`db.query.text` is now unconditional** — Npgsql emits the stable flavour always, there is no
    option to turn it off, and qyl does not strip what a library wrote.
  - **MySqlConnector** -> source `MySqlConnector`, one `Execute` span per command and one `Open`
    span per `Open()` call, pooled or not. A failed command reports `error.type` and
    `db.response.status_code` as the **MySQL error number** (`1054`), with `Activity.Status` `Error`
    and `StatusDescription` the `MySqlErrorCode` name (`BadFieldError`); what it does not emit is an
    `exception` event, so the CLR exception type, message and stack trace never leave the process.
    The attribute flavour is the consumer's environment:
    without `OTEL_SEMCONV_STABILITY_OPT_IN=database` the span carries `db.system`, `db.name`,
    `db.statement` and `net.peer.*` instead of `db.system.name`, `db.namespace`, `db.query.text`
    and `server.*`. This is documented, not mapped — a processor that rewrote them would be
    inventing convention data the library did not emit.
  - **MySql.Data** -> source `connector-net`, one span per command, and every one of them is named
    `SQL Statement`. It reports failure as `otel.status_code` = `ERROR` plus an `exception` event
    while leaving `Activity.Status` `Unset`, so an exporter that reads the status sees a failed
    command as OK. It sets `db.statement` to the full command text unconditionally. A physical
    connection open additionally emits five `SQL Statement` spans for the driver's own handshake
    queries and one `Connection (pooled)` span that stops at `Close`.
  - **Oracle ODP.NET** -> source `Oracle.ManagedDataAccess.Core`, **two** spans per command: a root
    named after the method (`ExecuteScalar`) and its child `SendExecuteRequest`. Without the
    `Oracle.ManagedDataAccess.OpenTelemetry` add-on the whole vocabulary is `db.system`,
    `db.odp.roundtrip.count`, `db.odp.roundtrip.duration` (a `TimeSpan`) and
    `db.response.returned_rows`; `db.name`, `db.user`, `db.statement`, `server.address`,
    `server.port` and `error.type` are gone. `QYL1002` says so at compile time.
    `OTEL_DOTNET_AUTO_ORACLEMDA_SET_DBSTATEMENT_FOR_TEXT` is `not_implemented`: ODP.NET's own
    `SetDbStatementForText` owns that decision now.
  - **`db.client.operation.duration` is no longer produced for Npgsql.** The interceptor was its
    only producer, exactly as with the NServiceBus histogram in 14.0.0, so `signals.metrics.NPGSQL`
    becomes `control_bound`: a consumer who wants Npgsql's own instruments registers them through
    `OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES`. SqlClient, ADO.NET and Sqlite keep producing it.
- **GraphQL.NET** -> source `GraphQL`. `QylInterceptedGraphQl` and its GraphQL document parser are
  deleted. It is the third bucket: the source stays silent until the application calls
  `UseTelemetry()` on its own `IGraphQLBuilder`, and qyl does not call it — an interceptor that did
  would be the mechanism this rule removes. `OTEL_DOTNET_AUTO_GRAPHQL_SET_DOCUMENT` is
  `not_implemented`; GraphQL.NET's own `UseTelemetry(o => o.RecordDocument)` owns the document.
- **`AddQyl()` is idempotent.** Every call used to queue another `WithTracing`/`WithMetrics`/logging
  callback, so an application that called it and then called something that calls it — `AddQylApi`
  does — built two `BatchExportProcessor` + `OtlpExporter` pairs, two `QylSessionSpanProcessor` and
  two `QylNativeSpanProcessor`, and exported every span twice. A marker service registered with
  `TryAdd` makes the second call return the builder untouched. **First call wins**; a later call's
  `QylSdkOptions` are ignored, silently, because the composing library and the application both ask
  for the defaults and neither is wrong.
- **`AddQyl()` no longer blocks on collector discovery.** The probe cost about 400 ms — four 100 ms
  TCP connect attempts plus a DNS lookup for `qyl` — on the caller's thread in every process without
  a reachable collector, which is a cold-start cost every Qyl API pays. Discovery now starts on the
  thread pool and the exporter reads its result in the options callback the SDK invokes when it
  builds the pipeline; `AddQyl` itself touches no socket. `RequireConfiguredEndpoint` keeps its
  meaning — no endpoint means do not export — and is the one caller that still resolves the probe
  eagerly, because for it the exporter is either registered or it is not.
- **A build-time host exports nothing.** `GetDocument.Insider` runs the application's startup path
  at build time to read its OpenAPI document, inside the developer's or CI's environment —
  `OTEL_EXPORTER_OTLP_ENDPOINT` included — so every build shipped build-time spans and log records
  to the real collector. **The mechanism is one gate, checked before any provider is registered**:
  `HostApplicationBuilder` takes `ApplicationName` from the entry assembly, and under that host the
  entry assembly is the tool, so the name identifies it without reflection. When it matches, no
  exporter is registered on any of the three signals and no discovery probe is started, whatever the
  environment or the caller's `CollectorEndpoint` says. Everything else — endpoint resolution for a
  normal host — happens in the `AddOtlpExporter` configure delegates the SDK invokes at provider
  build, and the logging provider is gated by the same flag. `dotnet ef` and `WebApplicationFactory`
  are **not** covered and this is deliberate: they run the application as its own entry assembly, so
  nothing distinguishes them from the real host. A design-time host that must not export sets
  `QylSdkOptions.RequireConfiguredEndpoint` and leaves the endpoint unset. The qyl SDK's own
  `QylBuildTimeDocumentHost` workaround is redundant once it pins `15.0.0`.
- **`AddQyl()` deduplicates `AdditionalSources` and `AdditionalMeters`** against what it already
  subscribed, so a consumer naming `System.Net.Http` again does not double-subscribe.
- **`session.id` is a span tag and never baggage.** The README and the `AddQyl` summary said it was
  "propagated across traces"; nothing in `Qyl.Telemetry.Hosting` puts it on the wire. It is copied
  onto the descendants of a tagged span within the process, and the next process sees it only if
  the application put it in baggage itself.

### Removed

- `QylInterceptedHttpClient`, `QylInterceptedGraphQl`, `HttpClientDiagnosticListener`,
  `GrpcClientDiagnosticListener`, `GrpcClientPayloadReader`, `HttpSemantics`, `QylGrpcSemantics`
  and `QylSignalOwnership`.
- The `HttpClient` and `GraphQlExecute` shape predicates and the `Forward` interceptor body
  template: no declaration selects them any more, so they are gone from `QylShapes`,
  `QylInterceptorBody` and the generator. `QylInterceptorBody.DbCommand` changes value from `2`
  to `1`.
- **The last reflection in the productive code.** `GrpcClientPayloadReader` was the one sanctioned
  `System.Reflection` site in the whole package family, and it went with the listener it served.
  The contract-invariants gate no longer carries an exception for it.
- The `Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners` package loses its HttpClient and
  gRPC lanes. What remains of it is the base subscriber and the shared semantics helpers that the
  EF Core and SqlClient packages build on.

### Added

- **`QYL1002`**, an `Info` diagnostic from the source generator: a compilation uses a library whose
  native `ActivitySource` qyl subscribes but never makes the call that library needs before it
  emits, or before it emits in full. One parameterised id, one table: GraphQL.NET's
  `IGraphQLBuilder.UseTelemetry()` (without it, no spans at all) and ODP.NET's
  `AddOracleDataProviderInstrumentation()` (without it, the four-key vocabulary above). The
  generator is the only qyl component that sees the consumer's call sites, so it is the only one
  that can tell "this application uses the library" from "this application opted its telemetry in".
  **MySql.Data has no row on purpose**: `MySql.Data.OpenTelemetry`'s `AddConnectorNet()` is
  `AddSource("connector-net")` and nothing else, which is exactly what `AddQyl()` already does.
- `QylIntercept.NativeSourceReceivers`, the declaration data that keeps the ADO.NET interceptor off
  the four providers whose own source qyl subscribes.
- A `native_source` lane in the ownership contract, replacing `framework_initialization`: the name
  now says what the row means — the library owns the `ActivitySource`, `Qyl.Telemetry.Hosting`
  subscribes it, `QylNativeSpanProcessor` stamps the domain, and nothing is intercepted or
  rewritten. Fifteen rows carry it. A `native_source` row must name the source table, the processor
  and a real demo verifier as evidence, which the gate checks.

### Gates

- Every touched `verify-real-*-demo.py` asserts the **exact** multiset of `(scope, kind)` tuples for
  each operation across **all** scopes, not a filtered view of one scope: a third source producing a
  span for the same operation fails the gate. The only excluded prefix is `Experimental.`, the
  runtime's own socket, DNS, TLS and connection diagnostics, which qyl never subscribes.
- The generator snapshot fixture moves off `HttpClient.GetAsync` to `DbCommand.ExecuteScalar` — the
  ADO.NET lane is what stays an interceptor — and `QYL1001` is proven there.
- The semantic-convention pin moves to `9.3.0`, which adds the `Grpc.Net.Client` vendor source
  constant and the `grpc.method` / `grpc.status_code` vendor keys, and removes the `qyl.http.client`
  event: it had no producer — its only use was an event-name comparison inside the deleted
  HttpClient listener, and it was never emitted as a span event at all.

## [14.1.0] - 2026-09-07

### Changed

- **One server span per request, and it is ASP.NET Core's own.** `AddQyl()` subscribes
  `Microsoft.AspNetCore`, and the qyl ASP.NET Core middleware started a second SERVER activity for
  the same request, so a consumer exported two: the hosting root carrying nothing, and a qyl span
  beneath it carrying the route. This package's own contract says "exactly one server span per
  request", and the mapping rule says a library with a native `ActivitySource` gets `AddSource` plus
  enrichment, never a qyl-made duplicate. **The qyl server span is deleted.**
  `Microsoft.AspNetCore.Hosting.HttpRequestIn` is the server span. Its source stays
  `Microsoft.AspNetCore`, so a dashboard or an alert that keyed on the scope name
  `Qyl.Telemetry.AutoInstrumentation` for HTTP server spans has to move to the framework's.
- **The middleware writes what the runtime leaves empty.** Measured on .NET 10.0.11: the hosting
  activity carries *no tag at all* and keeps `Microsoft.AspNetCore.Hosting.HttpRequestIn` as its
  display name. `AddQylAspNetCoreInstrumentation()` — which `AddQyl()` calls — registers the
  `IStartupFilter` that sets, from the `HttpContext` it already holds:
  `qyl.instrumentation.domain` = `aspnetcore.server`, `http.request.method` (and
  `http.request.method_original`), `url.scheme`, `url.path`, `url.query` under the existing
  redaction control, `http.route` and the `{method} {route}` span name once routing has resolved the
  endpoint, `http.response.status_code`, and the configured request and response headers. Every one
  of them fills only what is absent, so a tag another component wrote survives, and a future runtime
  that sets these itself silently takes them over.
- **Consumers of `Activity.Current` in middleware now see the hosting activity**, whatever the
  registration order. `Qyl.Api`'s session-baggage filter is the case that mattered: its `session.id`
  landed on whichever span qyl had made current, and now lands on the request's root, from where
  `QylSessionSpanProcessor` — unchanged — copies it onto every child span. A Qyl.Api session that
  used to return six spans for three requests returns three.
- **`error.type` on an unhandled exception is the exception type name**, not the status code the
  server sends after it — the exception is what failed the request. A route that throws
  `InvalidOperationException` now reports `System.InvalidOperationException` where it reported
  `500`; `http.response.status_code` is `500` either way.
- **The ASP.NET Core `DiagnosticListener` lane is deleted**, and `QylAspNetCoreOwnership` with it.
  It created the same duplicate whenever the middleware was not registered, and the ownership flag
  existed only to arbitrate between two lanes that no longer both emit. An application that
  references `Qyl.Telemetry.AutoInstrumentation.Hosting` without wiring the SDK therefore adds
  `services.AddQylAspNetCoreInstrumentation()` and `AddSource("Microsoft.AspNetCore")` to keep
  server spans; `AddQyl()` does both on its own.
- **The demo lane answers to the contract.** `signals.traces.ASPNETCORE` has declared an
  `aspnetcore.server` conformance signal all along and nothing read it. `verify-real-aspnetcore-demo.py`
  now takes its required attributes from `docs/contracts/qyl-aot-ownership.yaml` and holds every run
  — managed and NativeAOT, with and without the capture opt-in — to exactly one SERVER span per
  request, from `Microsoft.AspNetCore`, named after its route and carrying all five. The demo itself
  runs the real registration path (`AddQyl` plus an in-memory exporter) and proves the session
  reaches child spans.
- **The semantic-convention pin moves to `9.2.0`** across `Qyl.Telemetry.SemanticConventions`,
  `.Incubating` and `.Analyzers`, with the live-check workflows' registry ref. The two framework
  source names the runtime owns, `Microsoft.AspNetCore` and `System.Net.Http`, are not registry rows;
  they now live in one internal `QylFrameworkActivitySources` instead of being typed in two places.

## [14.0.1] - 2026-09-07

`14.0.0` was tagged and never published: its release gate failed in the `verify` job, which ran
the live check without weaver on `PATH` and without the pinned registry. nuget.org still tops out
at `12.0.0`, so `14.0.1` is the first release of this wave and carries everything the `14.0.0`
entry below describes, plus the three changes here.

### Fixed

- **The live check stopped judging spans partway through and still passed.** weaver's
  `--inactivity-timeout` was 60s, shorter than the silence between two lanes: nothing reaches the
  listener while the MassTransit lane pulls RabbitMQ and NativeAOT-publishes. The listener
  self-terminated — 184s in, exit code 0 — port 4317 closed, and every lane after it exported into
  a closed port while still passing its own in-memory assertions. Four bounds close it. The
  inactivity timeout is derived from a new per-lane bound and larger than it, so the lane bound is
  always the enforcer and weaver cannot end the run. The lane subprocess carries that bound, so a
  hung demo is reported rather than waited on. The listener is polled before and after every lane,
  and one that has exited stops the gate with weaver's own exit code — with weaver's `0` reported
  as `1`, because an inactivity exit is exactly the case that must not read as success. And the
  CoreWCF demo, the one lane with no explicit flush, force-flushes with the same 5s bound the
  other eight use instead of relying on host disposal, which shuts the batch processor down with
  `Timeout.Infinite`.

### Changed

- **The registry levels a live-check finding, not this repository.** The gate runs
  `weaver registry live-check` with both halves of the policy set the pinned semantic-convention
  release owns: `--config registry/.weaver.toml` drops weaver's built-in deprecated, type and enum
  findings by id — those advisors are compiled into the binary and emit at a level no policy can
  lower — and `--advice-policies registry/policies/live_check_advice` re-issues them at the level
  the registry chose. An open enum carrying `_OTHER` is information, a renamed or obsoleted key a
  library emits is an improvement, a type mismatch whose value parses is an improvement.
  `--fail-on violation` is unchanged and there is no allowlist in the gate; a pin missing either
  half fails it rather than silently restoring weaver's own verdict. Against the `v9.1.0` registry
  the demo lanes report 403 advisories — 364 improvement, 39 information, zero violations.
- **The semantic-convention pin moves to `9.1.0`** across `Qyl.Telemetry.SemanticConventions`,
  `.Incubating` and `.Analyzers`. It brings the vendor models for the keys the first live-check run
  found undeclared — `soap.*` and `wcf.channel.*` from CoreWCF, `az.schema_url` and
  `az.client_request_id` from Azure.Core, `db.elasticsearch.schema_url` from the Elasticsearch
  client — and publishes `Azure.*` and `CoreWCF.Primitives` as
  `QylTelemetryNames.VendorActivitySources` constants. `QylTelemetrySources` typed both by hand;
  no `ActivitySource` name is a literal in this repository any more.
- **The gate fails when the registry checkout and the package pin are different releases.** The ref
  is named in two workflows and the version in `Directory.Packages.props`, and nothing tied them
  together; judging spans against a registry other than the one whose constants the code compiled
  against would have looked like a green gate.
- The README gains a live-check section, carrying the gap the gate has: the RabbitMQ lane only
  publishes, so `RabbitMQ.Client.Subscriber` emits nothing and the `deliver` and `fetch` spans the
  `14.x` line added are unjudged.

## [14.0.0] - 2026-09-07

Two waves in one release. Seven libraries stopped being intercepted and are subscribed to
instead, and the semantic conventions moved to the Weaver-only 9.0.0 registry. No `13.0.0` was
published: the native-source wave landed on `main` after the `v12.0.0` tag and never got a tag of
its own, so it ships here.

### Breaking changes

- **BREAKING: seven libraries emit their own spans now, not qyl's.** The rule is one question —
  does `AddSource("<name>")` alone deliver spans? — and where the answer is yes the interceptor is
  deleted and `Qyl.Telemetry.Hosting` subscribes to the library's `ActivitySource`, with one
  processor stamping `qyl.instrumentation.domain`. Every consumer dashboard keyed on the old
  source or span name has to move. Per library, old to new:

  | Library | Was | Is |
  | --- | --- | --- |
  | MassTransit | `Qyl.Telemetry.AutoInstrumentation` span `publish` / `send` | `MassTransit` span `{destination} send` |
  | Elastic.Transport | `Qyl.Telemetry.AutoInstrumentation` span `request` | `Elastic.Transport` span named after the HTTP method |
  | Elasticsearch | `Qyl.Telemetry.AutoInstrumentation` span `request` | `Elastic.Transport` span named after the client operation |
  | RabbitMQ.Client | `Qyl.Telemetry.AutoInstrumentation` span `publish {exchange}:{routing_key}` | `RabbitMQ.Client.Publisher` span `publish {routing_key}`, plus `RabbitMQ.Client.Subscriber` `deliver` and `fetch` |
  | MongoDB.Driver | `Qyl.Telemetry.AutoInstrumentation` span `{operation} {collection}` | two nested `MongoDB.Driver` spans, an operation span and the wire-command span beneath it |
  | Quartz | `Qyl.Telemetry.AutoInstrumentation` span `{group}.{name}` | `Quartz` span `Quartz.Job.Execute`, plus `Quartz.Job.Veto` and the job-store spans |
  | NServiceBus | `Qyl.Telemetry.AutoInstrumentation` spans `send` / `publish` | `NServiceBus.Core` spans `send message` and `publish event`, plus `process message` and a span per handler type |

  What the native spans do not carry is not reconstructed. MassTransit reports the transport in
  `messaging.system` (`rabbitmq`, not the qyl-owned `masstransit`) and the deprecated
  `messaging.operation`, always `send`, so `Publish` and `Send` are no longer distinguishable.
  Elastic.Transport emits no database conventions of its own. NServiceBus emits no messaging
  conventions at all and reports failure as `otel.status_code`. `error.type` is gone from all
  seven. In exchange the vendor namespaces arrive — `messaging.masstransit.*`,
  `elastic.transport.*`, `quartz.*`, `nservicebus.*`, `db.mongodb.*`,
  `messaging.rabbitmq.delivery_tag` — and so do the consumer-side spans the interceptors never
  produced.

- **BREAKING: `nservicebus.messaging.operation.duration` and the
  `Qyl.Telemetry.AutoInstrumentation.NServiceBus` meter are gone.** The interceptor was the
  histogram's only producer. NServiceBus publishes its own instruments on `NServiceBus.Core` and
  `NServiceBus.Core.Pipeline.Incoming`; a consumer that wants them registers them through
  `OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES`, exactly as for the native `Npgsql` meter.
  `signals.metrics.NSERVICEBUS` becomes `control_bound`.

- **BREAKING: the instrumentation no longer normalises what a library emitted.** It writes
  `qyl.instrumentation.domain` and nothing else onto a native span. Three normalisers are deleted
  rather than moved:
  - CoreWCF's deprecated `rpc.system` is exported as CoreWCF wrote it. The collector's
    `AttributeMapping.TryGetRename` maps it to `rpc.system.name`.
  - Azure SDK spans keep `url.full` and the namespace-qualified `error.type`
    (`Azure.RequestFailedException`, not `RequestFailedException`).
  - Elastic.Transport and Elasticsearch share one source and now one domain, `elastic.transport`.
    `qyl.instrumentation.domain` no longer resolves to `db.elasticsearch` per span:
    `elastic.transport.product.name` is a vendor pass-through key in the 9.0.0 registry, so the
    distinction is drawn where the mapping table is.

- **BREAKING: the generated-code ABI anchor moves from `QylGeneratedCodeAbi.V12` to
  `QylGeneratedCodeAbi.V14`**, tracking the package major. Generated interceptors built against a
  `12.x` runtime fail to compile against `14.x` rather than binding to it.

### Changed

- The semantic-convention pin moves to `9.0.0` across `Qyl.Telemetry.SemanticConventions`,
  `.Incubating` and `.Analyzers`. Weaver is the only generator behind them and the four-package
  family becomes three — `.SourceGeneration` is retired at `8.1.0`, and this repository never
  referenced it. Every constant read here keeps its name.
- `db.client.operation.duration` is created from
  `Metrics.DbMetricDefinitions.DbClientOperationDuration`, name and unit together. `QylMetricNames`
  held nothing else and is deleted; no metric name is spelled in this repository any more.
- `DESIGN.md` is deleted. Its audit is a section of the workspace `DECISIONS.md`; the version
  floors it owned belong in the registry's vendor annotations, and `supported_versions` in
  `docs/contracts/` is the generated copy that remains.

### Added

- **`live check`**, a gate that judges what this package emits against the registry it pins.
  `tools/verify-live-check.py` starts `weaver registry live-check`, points the nine native-source
  demo lanes at its OTLP listener and exits with weaver's verdict at `--fail-on violation`;
  `.github/workflows/live-check.yml` runs it with Weaver `0.26.1` pinned by commit and the registry
  checked out at the tag the package pin names. There is no allowlist in the gate.

  Its first run reports no key this package writes. Every finding is a library's own output on a
  span qyl subscribes to, and the fix for each is a registry declaration, not a rewrite here:

  | Source | Finding |
  | --- | --- |
  | `CoreWCF.Primitives` | `rpc.system` deprecated for `rpc.system.name`; `soap.message_version`, `soap.reply_action`, `wcf.channel.path`, `wcf.channel.scheme` undeclared |
  | `Azure.*` | `az.namespace` deprecated for `azure.resource_provider.namespace`; `az.schema_url`, `az.client_request_id` undeclared |
  | `Elastic.Transport` | `db.system` deprecated for `db.system.name`; `db.operation` deprecated for `db.operation.name`; `db.elasticsearch.schema_url` undeclared |
  | `MassTransit` | `messaging.operation` deprecated for `messaging.operation.type`; `messaging.message.body.size` written as a string where the registry types it `int` |
  | `NServiceBus.Core` | `exception.escaped`, obsoleted with no replacement |

  `Quartz`, `RabbitMQ.Client.Publisher` and `Qyl.Telemetry.AutoInstrumentation` itself report none.
  `MongoDB.Driver` is unreported: `mongo:8-noble` refuses to start on a Linux kernel 6.19 or newer
  (SERVER-121912), so that lane did not run on the machine this list was taken from.

- Two enforcers in `verify-contract-invariants.py`. `verify_system_value_contract` fails when a
  `messaging.system`, `rpc.system.name` or `db.system.name` value is written as a literal, or when
  an emitting source spells a registry system value at all; the one recorded gap is `dotnet_wcf`,
  which `rpc.system.name` does not enumerate, allowed by file and required to keep the comment
  naming the gap. `verify_metric_contract` now requires every instrument to be created from a
  `MetricDefinition` row rather than from a constants file.
- The demo lane reads `QYL_LIVE_CHECK_ENDPOINT` for its collector endpoint, which is how the live
  check receives what the demos assert on. `Qyl.RealCoreWcfDemo` disposes its host instead of only
  stopping it, so its exporter flushes.

## [12.0.0] - 2026-09-04

Every third-party pin moves to its current latest stable in one wave. One of those
moves changes what the published packages do rather than only what they build
against: Quartz 4 rewrote `IJob.Execute`, so the interception contract follows it and
drops the Quartz 3 shape.

### Breaking changes

- **BREAKING:** Quartz 3 jobs are no longer intercepted. Quartz 4 rewrote `IJob.Execute`
  into `ValueTask Execute(IJobExecutionContext, CancellationToken)`, and the generator's
  shape predicate `TryMatchQuartzJob` follows the method it was written for: it now
  requires a `ValueTask` return and exactly two parameters, `IJobExecutionContext`
  followed by `CancellationToken`, and the emitted interceptor's return type moves from
  `Task` to `ValueTask`. The Quartz 3 shape is deleted rather than kept alongside the new
  one, so an application still on `Quartz` 3.x compiles and runs with no qyl Quartz spans
  at all. `QylInterceptedQuartz` drops `ObserveAsync = true` with it — that flag routes
  through `QylInterceptedActivity.ObserveAsync`, which observes a `Task` — and the
  interceptor takes the generator's ordinary asynchronous path, forwarding the
  scheduler's cancellation token to the intercepted job. `signals.traces.QUARTZ` carries
  the qyl `supported_versions` override `Quartz ==4.0.0; source-visible
  IJob.Execute(IJobExecutionContext, CancellationToken) calls only` in place of
  upstream's `>=3.4.0`, which stopped being a claim qyl could keep once the shape
  changed.
- **BREAKING:** the generated-code ABI anchor moves from `QylGeneratedCodeAbi.V11` to
  `QylGeneratedCodeAbi.V12`, tracking the package major as it always does. Generated
  interceptors built against an `11.x` runtime fail to compile against `12.x` rather
  than binding to it.

### Changed

- Every third-party package in `Directory.Packages.props` that was behind the latest
  listed stable on nuget.org moves: `Azure.Storage.Blobs` `12.29.1` -> `12.29.2`,
  `Elastic.Clients.Elasticsearch` `9.5.0` -> `9.5.1`, `GraphQL` `8.8.4` -> `8.8.5`,
  `Microsoft.Agents.AI` and
  `Microsoft.Agents.AI.Workflows` `1.17.0` -> `1.20.0`, `MongoDB.Driver` `3.11.0` ->
  `3.11.1`, `NServiceBus` `10.2.8` -> `10.2.9`, `Quartz` `3.19.1` -> `4.0.0`,
  `Roslynator.Analyzers` `4.16.1` -> `5.0.0`, and `StackExchange.Redis` `3.1.13` ->
  `3.1.31`. `MassTransit.RabbitMQ` is the one deliberate exception and stays on
  `8.5.10`.
- The semantic-convention pin moves to `8.0.1` across
  `Qyl.Telemetry.SemanticConventions`, `.Incubating` and `.Analyzers`. It is a patch on
  the `8.0.0` vocabulary this package already consumed; no constant this package reads
  changes shape. Consumers receive `Qyl.Telemetry.SemanticConventions` and `.Incubating`
  `8.0.1` transitively; `.Analyzers` is `PrivateAssets="all"` and does not flow.
- The exact-version contract claims for `Microsoft.Agents.AI` and
  `Microsoft.Agents.AI.Workflows` move to `==1.20.0` in
  `docs/contracts/qyl-native-instrumentations.yaml`, and the generated coverage matrix,
  the resolved contract and the README table follow. These stay exact managed
  path/version claims, not provider-wide ones.
- MassTransit instrumentation is version-independent at the package level: the library
  references no MassTransit package; the generator binds the consumer's own MassTransit
  at compile time. Verified against MassTransit `8.5.10` (Apache-2.0). MassTransit 9 is
  commercially licensed and is neither referenced nor shipped by this package; the
  repository's demo pin stays on the 8.x line. `signals.traces.MASSTRANSIT` now carries
  the qyl `supported_versions` override `MassTransit 8.x (Apache-2.0); verified against
  8.5.10; source-visible IPublishEndpoint/ISendEndpoint/ISendEndpointProvider
  Publish/Send calls only` in place of upstream's `>=8.0.0`, which claimed a range this
  repository does not exercise.
- The NativeAOT publish gate pins each tolerated vendor diagnostic to its resolved
  package version, so the GraphQL and NServiceBus entries move with their packages.
  Approvals are keyed by diagnostic id, owning assembly, package, resolved version,
  marker and count; the MassTransit approvals stay on `8.5.10` with the pin.
- The published contract stops naming the retired `Qyl.Sdk`. `Qyl.Telemetry.Hosting` is
  the registration package in the `content_basis`, the twelve promise notes and the
  `primary_owner` fields of `docs/contracts/qyl-native-instrumentations.yaml`, and in the
  AZURE and WCFCORE owners of `docs/contracts/qyl-aot-ownership.yaml`; the contract's
  `release` had drifted three majors behind at `8.0.0`. Shipped since the `11.0.0` tag
  and recorded here.
- `demos/Qyl.RealQuartzDemo` follows the Quartz 4 rewrite: `ProbeJob`, `FailingJob` and
  `OuterJob` take the new signature and pass the token on, and the standalone scheduler
  comes from `QuartzSchedulerBuilder.Create(...).Build()` because `StdSchedulerFactory`
  is gone in Quartz 4.

## [11.0.0] - 2026-09-03

### Changed

- The semantic-convention pin moves to `8.0.0`, which removes the `qyl.agent.diagnostic.*` and
  `qyl.workflow.*` vocabulary and the `qyl.agent.diagnostic.snapshot` event — 14 attributes and one
  event that only the removed qyl Codex observer ever produced. Nothing in this package referenced
  any of them, so no producer code changes; the major exists because consumers of
  `Qyl.Telemetry.AutoInstrumentation*` receive the semantic-conventions major transitively. The
  generated-code ABI anchor follows the package major from `QylGeneratedCodeAbi.V10` to
  `QylGeneratedCodeAbi.V11`, so generated interceptors built against a `10.x` runtime fail to
  compile rather than binding to `11.x`.

## [10.1.0] - 2026-09-02

### Changed

- The semantic-convention pin moves to `7.1.1` and `Qyl.Telemetry.SemanticConventions.Analyzers`
  is referenced for the first time, with `OtelSemConvInstrumentationLibrary` set in
  `Directory.Build.props`. Two things in the package had to change before that was possible:
  7.1.0 added that opt-out for QYL0008, whose "copy the constant locally" advice is the
  hand-maintained duplication 10.0.0 deleted, and 7.1.1 stopped shipping
  `ANcpLua.Roslyn.Utilities.dll` beside the analyzers, which collided with the build of the same
  assembly that `ANcpLua.Analyzers` carries and made every rule fail to load here (CS8032). The
  opt-out also covers QYL0101, since this library's `ActivitySource`s are registered by
  `Qyl.Telemetry.Hosting`, not by its own compilation. Every other rule in the package now runs
  against the producer and reports nothing.

## [10.0.0] - 2026-09-02

### Changed

- The semantic-convention pin moves to `7.0.0`. `QylTelemetryNames` graduates out of
  `Qyl.Telemetry.SemanticConventions.Incubating` into the stable package under
  `Qyl.Telemetry.SemanticConventions.Names`, and its `Scopes` members are renamed with the strings
  they carry: `QylTelemetryAutoInstrumentation`, `QylTelemetryAutoInstrumentationDatabase` and
  `QylTelemetryAutoInstrumentationNServiceBus`. `QylActivitySource`, `QylMetricMeters` and the
  HttpClient listener follow the move; no scope name is spelled out anywhere in `src/` or `tests/`.
  `Qyl.Telemetry.SemanticConventions.Analyzers` 7.0.0 is deliberately NOT referenced: its QYL0008
  rule forbids a library from reading any Incubating constant and tells it to copy the value
  locally, which is the hand-maintained duplication this major deleted (138 diagnostics, one per
  legitimate call site), and QYL0101 reports the qyl `ActivitySource` as unregistered because the
  library that declares it is not the assembly that calls `AddSource`. QYL0200/QYL0201 raise
  nothing on this tree.
- The `6.0.0` pin that preceded it made the vocabulary packages
  consumers of their own Roslyn generator — the constants this package reads are now produced at
  build time from the pinned registry rather than checked in, and are byte-identical to the `4.4.0`
  surface, so nothing here changes shape. It also ships the object-first definition types
  (`MetricDefinition<TInstrument>`, `SpanDefinition<TKind>`, `EventDefinition`, `EntityDefinition`)
  in the compiled package, `AllValues`/`Contains` on every enum-value class, and
  `qyl.instrumentation.domain` as a generated value set. The typed `Activity` setters and the
  definition-to-BCL bridge are separate changes; the two surfaces below are adopted.
- `QylInstrumentationDomains` is gone. Every `[QylIntegration]` and activity policy names its
  domain through `QylAttributes.InstrumentationDomainValues`, the generated value set the
  hand-written class had restated member for member since semconv `b8ddb0a`.
- The HTTP request-method vocabulary is read from `HttpAttributes.RequestMethodValues.Contains`
  instead of a hand-typed `HashSet` and a ten-branch `string.Equals` chain. One rule,
  `QylHttpMethod.IsKnown`, serves both the interceptor and the diagnostic listener; `_OTHER`
  stays unknown so `http.request.method_original` is still recorded for it.
- `tools/verify-contract-invariants.py` fails on any `"qyl.` literal under `src/` outside
  generated files, and `tools/verify-version-sync.py` fails when the CHANGELOG's top entry is
  neither `[Unreleased]` for an untagged version nor `[<props version>] - <date>`.
- **BREAKING:** the qyl scope names follow the `Qyl.Telemetry` package family. The single
  `ActivitySource` and the two qyl-owned meters become `Qyl.Telemetry.AutoInstrumentation`,
  `Qyl.Telemetry.AutoInstrumentation.Database` and `Qyl.Telemetry.AutoInstrumentation.NServiceBus`.
  The registry owns those strings, so the values arrive with the semantic-convention pin rather
  than from this repository: `src/` and `tests/` already read every scope name from
  `QylTelemetryNames.Scopes` and contain no literal of either spelling. A consumer that calls
  `AddSource(...)` or `AddMeter(...)` with `Qyl.OpenTelemetry.AutoInstrumentation`,
  `Qyl.OpenTelemetry.AutoInstrumentation.Database` or
  `Qyl.OpenTelemetry.AutoInstrumentation.NServiceBus`, or that sets
  `OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES` to them, must update. The qyl collector accepts both
  spellings for the lifetime of this major.
- **BREAKING:** `QylSemanticAttributes` is deleted. Seventy-seven
  `public const string X = SomeAttributes.Y;` re-aliases bought a second name for every attribute
  key and hid which registry each key came from; every runtime call site now names the generated
  semconv constant through a file-level `using` alias. The four values that were compositions
  rather than registry members — the `http.request.header.` / `http.response.header.` /
  `rpc.request.metadata.` / `rpc.response.metadata.` prefixes — are composed at their single call
  site in `QylAutoInstrumentationOptions`. The three system values the registry does not carry live
  with the code that writes them: `dotnet_wcf` on `QylInterceptedWcfClient` (`rpc.system.name`
  enumerates only `connectrpc`, `dubbo`, `grpc` and `jsonrpc`; `dotnet_wcf` exists solely on the
  deprecated `rpc.system`), and `masstransit` / `nservicebus` on `QylMessagingActivityPolicy`
  (`messaging.system` enumerates neither). The type was `internal`, so no public API moves.
- `messaging.operation.name` and `messaging.operation.type` are sourced consistently. The registry
  gives `messaging.operation.name` no value set — it is the system-specific verb, examples `ack`,
  `nack`, `send` — and marks the `publish` member of `messaging.operation.type` deprecated in
  favour of `send`. qyl writes its own two verbs (`send`, `publish`) as the operation NAME and
  always writes `MessagingAttributes.OperationTypeValues.Send` as the TYPE; the former
  `MessagingOperationNameSend = OperationTypeValues.Send` conflation — an operation-type value
  standing in for an operation name — is gone.
- `QylMetricMeters.GetEnabledMeterNames` reads a static instrumentation-id → meter-names table
  instead of a nine-branch `if` chain with a `databaseMeterEnabled` flag and two dedup strategies.
  Output order and duplicate handling are unchanged; `tools/verify-environment-options-behavior.py`
  pins the exact emitted list for six option sets, and `tools/verify-contract-invariants.py` now
  reads the registered set out of that table.

### Removed

- **BREAKING:** the log-as-span lane is deleted. qyl intercepted `ILogger`, NLog and log4net calls
  and started an `Activity` named `ILogger log` / `NLog log` / `log4net log` carrying one tag,
  `log.severity`. That attribute is in no semantic-convention registry and is absent from the qyl
  collector's span allowlist, so it was discarded at ingest — leaving a span with no message, no
  body and no surviving attribute, while `Qyl.Telemetry.Hosting` already exported the same calls as
  real OTLP LogRecords through `builder.Logging.AddOpenTelemetry()`. Gone from the ABI surface:
  `QylInterceptedILogger`, `QylInterceptedNLog`, `QylInterceptedLog4Net`, the `Log`, `LogExtension`
  and `ExternalLog` body templates, and the `LoggerLog`, `LoggerExtension` and `ExternalLogger`
  shapes. Gone from the runtime: the `log.ilogger`/`log.nlog`/`log.log4net` domains, the `ILOGGER`,
  `NLOG` and `LOG4NET` instrumentation ids with their
  `OTEL_DOTNET_AUTO_LOGS_{id}_INSTRUMENTATION_ENABLED` toggles, and the `log.severity` constants.
  `Qyl.RealNLogDemo` and `Qyl.RealLog4NetDemo` and their verifiers are deleted with it.
- `signals.logs.ILOGGER` stays `implemented`: `AddQyl()` satisfies the upstream promise by exporting
  ILogger output as LogRecords, so the span lane was a second, wrong implementation of one row. The
  row now names the OpenTelemetry ILogger provider `Qyl.Telemetry.Hosting` registers, on the
  `runtime_public_telemetry` lane, and `Qyl.RealILoggerDemo` proves it under NativeAOT: six
  LogRecords with the expected severity and body, and zero qyl activities for a log call.
- `signals.logs.NLOG` and `signals.logs.LOG4NET` become `research_required` on a new
  `not_implemented` lane. NLog and log4net are independent logging frameworks; capturing either as
  LogRecords needs a bridge into `Microsoft.Extensions.Logging`, or a framework-specific
  appender/target, that qyl does not ship.

### Changed

- `OTEL_DOTNET_AUTO_LOGS_INSTRUMENTATION_ENABLED` now gates something. `AddQyl()` registers the
  OpenTelemetry ILogger provider only when both `QylSdkOptions.EnableLogExport` and that variable
  allow it; with the variable false the provider is not registered and no LogRecord is exported.
  A contract row that names a control which disables nothing is a lie, and `signals.logs.ILOGGER`
  is only implemented because Hosting exports those records.
- **BREAKING (generated-code ABI):** the generated-code ABI anchor moved from
  `QylGeneratedCodeAbi.V9` to `QylGeneratedCodeAbi.V10`, and the package major moved to `10.0.0`.
  Generated interceptors now reference the `V10` runtime anchor, so a stale generated interceptor
  from a `9.x` generator fails to compile against the `10.x` runtime instead of binding to changed
  behavior.
- **BREAKING (generated-code ABI):** the interceptor catalog is declarative. Each intercepted
  integration is one `QylIntercepted*` class in the runtime carrying `[QylIntegration(id, domain)]`
  and one `[QylIntercept(receiverType, methods…, Shape = QylShapes.X, Start = nameof(…))]` per call
  shape; the helper's parameters carry `[QylFromArgument]`, `[QylFromReceiver]`,
  `[QylFromMethodName]`, `[QylFromInstrumentationId]`, or `[QylFromShape]` bindings. The source
  generator reads those declarations from the referenced runtime assembly's metadata at compile
  time (ordinary Roslyn symbol reading — no runtime reflection) and derives the interceptor kind,
  the receiver and method matcher, the emitted body, the `Start`/`Enrich`/`Metric` arguments, the
  instrumentation id, domain, signal, and the contract manifest from them. The hand-coded
  `InterceptorKind` enum, the per-integration `TryGet*Invocation` matchers, and the two catalog
  tables are gone; what remains in the generator is the closed set of body templates
  (`QylInterceptorBody`: `Trace`, `Forward`, `DbCommand`)
  and the named shape predicates (`QylShapes`) that validate a library overload. The runtime's
  per-signal enabled-id sets are generated from the same declarations plus `[QylSignal]` rows for
  the listener, middleware, meter, and library-native lanes, so an integration cannot be added
  without its environment toggle. Consequences for the ABI surface: `QylInterceptedElastic` split
  into `QylInterceptedElasticsearch`/`QylInterceptedElasticTransport`,
  the fourteen `RecordException` copies and four `ObserveAsync` forwarders collapsed into
  `QylInterceptedActivity`, and each helper's start method is named for its operation (`Send`,
  `Publish`, `Command`, `Execute`, `Request`, `Call`, `Operation`). The manifest's
  `interceptorKind` is now `{Integration}.{Start}` (for example `Kafka.Send`, `Redis.Command`,
  `HttpClient.Forward`). `HttpClient` keeps the forwarding body: its helper mirrors the BCL's null
  semantics before any span starts, resolves the request URI against `BaseAddress`, enriches from
  the typed response, and maps `HttpRequestException.StatusCode`, none of which the trace template
  expresses. A synchronous `IMongoCollection<T>` call intercepted with runtime task observation
  previously never disposed its activity; the trace template now observes only asynchronous calls
  and disposes synchronous ones in `finally`.
- Messaging destinations are now emitted from the call site. Kafka produce spans carry
  `messaging.destination.name` (the topic, taken from the `string` or `TopicPartition` argument) and
  `messaging.destination.partition.id` when a `TopicPartition` is given; the span name is
  `send {topic}`. RabbitMQ publish spans carry `messaging.rabbitmq.destination.routing_key` and set
  `messaging.destination.name` to `{exchange}:{routing_key}` per the RabbitMQ convention — the
  available one alone when the other is empty, `amq.default` only when both are empty — with the span
  name `publish {destination}`. The message key is never emitted (unbounded).
- MongoDB spans carry `db.collection.name` (from the `IMongoCollection<T>` receiver's
  `CollectionNamespace.CollectionName`) and `db.namespace` (from `Database.DatabaseNamespace.DatabaseName`);
  the span name and `db.query.summary` become `{operation} {collection}`. Redis spans carry
  `db.namespace` set to the receiving database's index (never in the span name). WCF client spans
  carry `server.address` and `server.port` from `ClientBase<T>.Endpoint.Address.Uri`. Quartz execute
  spans are named `{JobDetail.Key.Group}.{JobDetail.Key.Name}` from the `IJobExecutionContext`
  argument and keep `Internal` kind. Elasticsearch's registry `http.request.method`/`url.full` are
  not observable at the client-method call site and are deliberately not emitted.
- Span names are derived from the span's own attributes, following each OpenTelemetry family's
  naming rule, and `QylActivityNames` is deleted. Database spans are named by `db.query.summary`
  (`SELECT Probe`, `INSERT Items`), which is now a real summary — the leading keyword and the first
  schema object it targets, found by a fail-closed scan that ends at the first quote, comment,
  parameter, temp-table, or dialect marker so a value can never reach it — instead of
  the bare verb, or the EF Core command source / SqlClient command type that used to be smuggled
  into it. Redis, MongoDB, and Elasticsearch spans are named by their operation; WCF by `rpc.method`;
  GraphQL by `graphql.operation.type` (`query`, `mutation`, `subscription`) of the operation the request
  executes — the one `operationName` selects, else the first — which is now emitted, or
  `GraphQL Operation` when the document has none; messaging by `{operation} {destination}`.
  `db.operation.name` is the statement's leading keyword (`PRAGMA`, `BEGIN`, `SET` included) rather
  than a guess from the ADO.NET method name or `_OTHER`. GraphQL spans stay `Internal`: they nest
  inside the HTTP server span, and a second server span per request would be wrong, so the
  registry's `graphql.server` kind is not applied to them.
- Messaging: Kafka produce spans carry `messaging.operation.name = send` (the convention's first
  choice for Kafka); `Consume()` spans are `receive` operations and therefore `Client` spans, as the
  messaging conventions require for pull-based consumers, not `Consumer` spans. RabbitMQ publishes to
  a named exchange emit `messaging.destination.name` — the exchange that was already passed to the
  helper and dropped; the default exchange's destination is its routing key, which the helper does
  not receive yet, so those spans claim no destination. The two conflated operation-name constants
  in the facade go.
- HTTP client spans on both lanes emit `network.protocol.version`; ASP.NET Core server spans on the
  listener lane emit `url.scheme`, which the convention requires. EF Core maps `IBM.EntityFrameworkCore`
  to `ibm.db2` (the registry value, not `db2`) and unmapped relational providers to `other_sql`.
- The qyl scope names — the `Qyl.OpenTelemetry.AutoInstrumentation` source, the `.Database` and
  `.NServiceBus` meters, and the `System.Runtime` subscription — read from the registry-generated
  `QylTelemetryNames.Scopes` constants instead of literals kept in sync by comments. The invariant
  gate resolves those symbols the way the registry emitter forms them.
- Doc comments that restated their identifier ("Well-known … value", "Runs the … runtime helper",
  "Defines the qyl auto-instrumentation surface") and the NativeAOT remark repeated on 24 types
  are gone; the NativeAOT invariant lives in AGENTS.md. `QylInterceptedHttpClient`'s overloads
  inherit the documentation of the `HttpClient` overload each one mirrors. The README no longer
  states versions the props files own.
- `QylRuntimeProcessMetrics` is deleted. The .NET 10 runtime's built-in `System.Runtime` meter
  publishes every instrument that class hand-wrote (and eleven more), with units and descriptions,
  and `AddQyl()` already subscribed to that meter — so an application ran two producers on one
  meter name. They disagreed: qyl's `dotnet.gc.heap.total_allocated` was a gauge over
  `GC.GetTotalMemory(false)` where the runtime emits a counter over `GC.GetTotalAllocatedBytes()`,
  and qyl's `dotnet.gc.last_collection.heap.size` was one untagged point where the runtime emits
  one per generation. The OpenTelemetry SDK either merges such pairs into one aggregator or exports
  both streams under a `DuplicateMetricInstrument` warning; either way the series was wrong. qyl now
  subscribes and produces nothing on `System.Runtime`. `OTEL_DOTNET_AUTO_METRICS_NETRUNTIME_…` and
  `…_PROCESS_…` still gate the subscription. The two same-valued meter constants collapse into
  `QylMetricMeters.RuntimeMeterName`; `QylMetricNames` keeps only the two instruments qyl produces.
- `Qyl.RealNetRuntimeMetricsDemo` no longer references `OpenTelemetry.Instrumentation.Runtime`,
  whose net10.0 build emits only the legacy `process.runtime.dotnet.*` names. The gate now proves
  the runtime's own meter under NativeAOT — the fact the NETRUNTIME and PROCESS contract rows cite.
  As shipped in .NET 10.0.11, `dotnet.thread_pool.thread.count` and `dotnet.thread_pool.queue.length`
  are `ObservableCounter`s rather than the `updowncounter` the semantic conventions specify; qyl
  reports what the runtime emits.

## [9.1.0] - 2026-08-15

### Changed

- Rebuilt the family against `Qyl.Telemetry.SemanticConventions` and
  `Qyl.Telemetry.SemanticConventions.Incubating` `4.3.0` (OpenTelemetry semantic conventions
  `1.44.0`), keeping generated telemetry values tied to the current owner-generated vocabulary
  instead of the compatible but stale `1.0.0` floor.
- Aligned the OpenTelemetry SDK, in-memory and OTLP exporters, hosting extensions, and runtime
  instrumentation on the coordinated `1.17.0` release line, and updated the Python OTLP verifier's
  `opentelemetry-proto` dependency to `1.44.0`.
- Refreshed the exact NativeAOT vendor-warning provenance for the .NET `10.0.11` ASP.NET Core and
  NativeAOT runtime packs after confirming that diagnostic IDs, owning assemblies, and counts are
  unchanged.
- Removed an unused central `Microsoft.Extensions.Hosting.Abstractions` version and the superseded
  repository-local G1 vocabulary smoke script; the active vocabulary gate remains owner-driven by
  the qyl repository.
- Updated every workflow to the current commit-pinned `actions/checkout` v7 and
  `actions/setup-dotnet` v6 releases after restoring the repository's Renovate inheritance.

## [9.0.1] - 2026-07-27

### Changed

- Semantic-convention constants now come from `Qyl.Telemetry.SemanticConventions(.Incubating)`
  `1.0.0`, the family's renamed IDs, replacing the retired
  `Qyl.OpenTelemetry.SemanticConventions*` `4.0.0` pins.
- The last two hand-written vocabulary literals (`qyl.instrumentation.domain`,
  `qyl.http.client`) now flow from the generated constants; the G1 vocabulary smoke
  reports zero hits in producer scope.

## [9.0.0] - 2026-07-27

### Changed

- The package family now ships under the `Qyl.Telemetry.*` IDs. The retired
  `Qyl.OpenTelemetry.AutoInstrumentation*` and `Qyl.Sdk` packages remain frozen at `8.5.0`.
- gRPC client spans now follow OpenTelemetry semantic conventions 1.43: full `rpc.method`,
  string `rpc.response.status_code`, configured `server.*`, IP `network.peer.*`, and
  `error.type` for failures. Deprecated `rpc.service`, `rpc.grpc.status_code`,
  `peer.hostname`, and `peer.port` attributes are no longer emitted.
- The native `Grpc.Net.Client` DiagnosticSource is the sole gRPC telemetry owner. The imprecise
  source-generated gRPC wrapper and its runtime ABI were deleted.
- Real integration demos now verify semantic-convention keys through the generated
  `Qyl.OpenTelemetry.SemanticConventions` constants.

## [8.5.0] - 2026-07-26

### Added

- Collector discovery honors `QYL_ENDPOINT`, the documented fallback between
  `OTEL_EXPORTER_OTLP_ENDPOINT` and probing localhost. It names a collector for the qyl exporters
  alone, where the standard variable redirects every OTLP exporter in the process.

  This closes a gap the 8.4.0 fold exposed rather than caused. `QYL_ENDPOINT` was documented as an
  automatic-instrumentation knob, but the only code that ever read it was the collector
  repository's private copy of collector discovery — the published package never supported the
  variable its own documentation described. Deleting that copy is what surfaced it. The knob now
  lives with the discovery implementation, and the qyl repository stops documenting a binding it
  does not own.

  An unparseable value returns no endpoint instead of falling through to probing: "configured, but
  wrong" must not silently export somewhere the operator did not ask for.

## [8.4.0] - 2026-07-26

### Added

- `QylSdkOptions.RequireConfiguredEndpoint` makes "no endpoint resolved" mean "do not export"
  instead of falling through to the OTLP exporter's own `localhost` default. Defaults to false, so
  an ordinary application is unaffected — that default is the local qyl collector, which is the
  right guess. A process that is *itself* an OTLP destination needs the opposite: for the qyl
  collector the exporter's default port is its own ingest port, so an unconfigured export would
  feed its output back into its input. `CollectorSelfExportGuard` cannot catch that case because it
  inspects a set `OTEL_EXPORTER_OTLP_ENDPOINT`, and here the variable is absent.

- `QylSdkOptions.ServiceVersion` stamps `service.version` on the resource, and
  `QylSdkOptions.ResourceAttributes` merges arbitrary resource attributes (schema URL, deployment
  environment, capability flags). Previously `AddQyl()` could name a service but not version it,
  which left a telemetry regression un-attributable to a build.

- `QylSdkOptions.ConfigureTracing` runs after the qyl sources and processors are registered and
  before the OTLP exporter, so a processor added there observes spans on their way out. It exists
  for policy the producer side cannot know — which of *this* application's endpoints are noise.

All four exist because the qyl collector consumes this package for its own self-telemetry rather
than maintaining a second, drifting copy of the composition logic. Each has that consumer as its
executable owner; none is speculative surface.

## [8.3.0] - 2026-07-25

### Changed

- `Qyl.OpenTelemetry.SemanticConventions` and `.Incubating` move from 3.5.1 to 4.0.0. That major
  carries the upstream registry pin bump to semantic-conventions-genai `74fd2e0`, which adds
  `gen_ai.request.previous_response.id` (the prior response or interaction a request continues
  from — OpenAI Responses `previous_response_id`, Google GenAI Interactions
  `previous_interaction_id`). 4.0.0's breaking changes are analyzer-surface deletions and
  zero-consumer public-surface cleanup; this repository consumes only the stable attribute
  constants (`Code`, `Cpu`, `Db`, `Dotnet`, `Error`, `Graphql`, `Http`, `Messaging`, `Rpc`,
  `Server`, `Url`), so no call site changes.

- No new attribute is emitted here. This package family instruments ASP.NET Core, HttpClient,
  EF Core, SqlClient, Redis, Kafka, GraphQL, and the rest of its domain inventory; it does not
  author GenAI inference or agent spans. `gen_ai.*` spans reach qyl from the
  `Microsoft.Extensions.AI` / `Microsoft.Agents.AI` sources that `Qyl.Sdk` subscribes to, and
  `gen_ai.request.previous_response.id` is set by whichever client builds the request. Span
  validation in the demos uses required-tag and forbidden-key checks rather than a closed
  allowlist, so the new attribute flows through untouched.

## [8.2.0] - 2026-07-25

### Added

- `KeyExpireAsync` is instrumented. It reaches five wire commands from one method, which is why
  8.1.0 left it out: a null expiry reaches `PERSIST`, and StackExchange.Redis drops to
  second precision when the value carries no whole milliseconds, so the same call is `EXPIRE` or
  `PEXPIRE` (`EXPIREAT` or `PEXPIREAT` for the `DateTime` overload) depending on the argument.
  Measuring the rule against a live server showed it is the millisecond component rather than the
  tick remainder — `TimeSpan.FromTicks(TimeSpan.TicksPerSecond + 1)` still reaches `EXPIRE` — and
  that test reads an argument the call site already holds, so naming the command costs no
  allocation. `ExpireWhen` does not change the command; it becomes an NX/XX argument.
  All five outcomes are pinned by demo probes, including the sub-millisecond tick case.

### Changed

- A Redis command's call-site test is now an ordered set of branches rather than a single
  alternate, which is what let one method resolve five commands. No interceptor allocates to
  choose a command name.

## [8.1.1] - 2026-07-25

### Changed

- A `StringSetAsync` call site no longer allocates to name its command. 8.1.0 reported `SETNX`
  for the `ValueCondition.NotExists` overload by testing the argument at the call site, but
  `ValueCondition` carries neither an equality operator nor `IEquatable<T>`, so that test boxed
  on every intercepted `SET` — including calls made with tracing switched off. The method now
  reports `SET` throughout rather than charging every caller for the one branch that differs.
- The demo's wire comparison declares its exceptions per call site instead of per command, so
  tolerating that one `SETNX` no longer loosens the check for every other `SET`. The
  `ValueCondition.NotExists` site accepts `SETNX` and nothing else, which keeps the deliberate
  inaccuracy pinned: if StackExchange.Redis ever stops sending `SETNX` there, the gate fails.
  A delete still accepts `DEL` or `UNLINK`, because that choice belongs to the server rather
  than to the call site — a different kind of exception, and now visibly so.

## [8.1.0] - 2026-07-24

### Fixed

- StackExchange.Redis spans now report the command the server actually received. The generator
  previously mapped a method *name* to a command, so overloads that differ only in what they put
  on the wire all reported the same value: `StringGetAsync(RedisKey[])` reported `GET` instead of
  `MGET`, `HashGetAsync(key, RedisValue[])` reported `HGET` instead of `HMGET`, the
  floating-point `StringIncrementAsync`/`StringDecrementAsync` overloads reported `INCR`/`DECR`
  instead of `INCRBYFLOAT`, an increment by a value other than one reported `INCR` instead of
  `INCRBY`, and `KeyTimeToLiveAsync` reported `TTL` instead of `PTTL`. Command selection now
  resolves per overload, and the overloads whose command depends on an argument value carry a
  call-site test.
- `IDatabaseAsync.ExecuteAsync` no longer reports the literal `EXECUTE`, which is not a Redis
  command. The span now names the command the caller passed, normalized the way
  StackExchange.Redis normalizes it. Command text that is not a single token is left off the
  span rather than becoming an unbounded span dimension.
- `HashSetAsync(RedisKey, HashEntry[])` is instrumented. It returns a non-generic `Task`, and the
  Redis matcher previously required `Task<T>`, so that one overload was silently skipped while
  the neighbouring single-field overload was traced.

### Added

- StackExchange.Redis interceptor coverage grew from 17 methods to the string, hash, key, list,
  set, and sorted-set surface of `IDatabaseAsync`. An overload the command table does not map is
  not instrumented, rather than instrumented under a guessed command name.
- `demos/Qyl.RealRedisDemo` registers a StackExchange.Redis profiling session and asserts every
  span's `db.operation.name` against `IProfiledCommand.Command` for the same call. The command
  table is verified against a live server instead of being maintained by hand. The one mapping a
  compile-time interceptor cannot name exactly is a delete: StackExchange.Redis substitutes
  `UNLINK` for `DEL` against a server that supports it, so the demo accepts either.

## [8.0.1] - 2026-07-22

### Fixed

- `Qyl.Sdk` now installs the qyl ASP.NET Core startup filter as part of `builder.AddQyl()`.
  A clean application previously booted listeners and exporters but omitted the qyl-owned
  inbound server span unless it added a second instrumentation registration itself.
- Updated the WCF demo's transitive `System.Security.Cryptography.Xml` floor to 10.0.10 so
  the release graph contains the patched .NET 10 servicing line.

## [8.0.0] - 2026-07-19

Intentional breaking convergence plus version-pinned telemetry paths. The new AI,
MCP, and CoreWCF entries below describe only the exact library versions and hooks
exercised by repository evidence; they are not provider- or protocol-wide claims.
The exact `ModelContextProtocol` 1.4.1 client/server path has strict NativeAOT
evidence; the other new paths have managed evidence only.

### Breaking changes

- **BREAKING:** the generated-code ABI anchor moved from
  `QylGeneratedCodeAbi.V6` to `QylGeneratedCodeAbi.V8`. Generated interceptors now
  require the V8 runtime anchor, so mixing an 8.x generator with a 6.x runtime (or
  the reverse) fails compilation.
- **BREAKING:** deleted the orphan `QylInterceptedWcfCore` generated-code helper and
  its unused policy/domain/name tail. No generator called it; CoreWCF server spans
  now use the official `CoreWCF.Primitives` `ActivitySource` path.
- **BREAKING:** deleted the generated HttpWebRequest, ASP.NET endpoint-map,
  EF Core, Azure client, and `MeterProviderBuilder.AddMeter` interceptor lanes and
  their unused generated-code helpers. Runtime listeners, specialist packages,
  explicit SDK meter registration, and first-party Azure sources are now the single
  owners of those signals. The HttpClient and gRPC client interceptor lanes remain
  the call-site owners of outbound HTTP/gRPC spans, header/metadata capture, and
  URL redaction; their completion listeners defer per signal ownership.
- **BREAKING:** deleted the DiagnosticListeners package's synthetic `qyl.db.efcore` and
  `qyl.db.sqlclient` demo listeners. Real EF Core and Microsoft.Data.SqlClient events
  are owned only by their dependency-isolating specialist packages.
- **BREAKING:** deleted the custom HTTP duration producer; the
  `System.Net.Http/http.client.request.duration` instrument is authoritative. The
  NServiceBus qyl meter is now `Qyl.OpenTelemetry.AutoInstrumentation.NServiceBus`,
  and `Qyl.Sdk` no longer force-registers the library-native `Npgsql`,
  `NServiceBus.Core`, and `NServiceBus.Core.Pipeline.Incoming` meters — consumers
  that want those instruments exported register them via
  `QylSdkOptions.AdditionalMeters`. MCP metrics are likewise outside the 8.0.0
  contract (mcp spans are registered; the `Experimental.ModelContextProtocol` meter
  is consumer-registered).
- **BREAKING:** renamed the public diagnostic extension base
  `DiagnosticListenerSubscriber` to `QylDiagnosticListenerSubscriber`. The five
  concrete ASP.NET Core, EF Core, gRPC client, HttpClient, and SqlClient listener
  types are internal; the abstract qyl-prefixed subscriber remains the supported
  extension surface.

### Changed

- Every emitted interceptor now carries one adjacent machine-readable JSON manifest
  containing its interceptor kind, signal, instrumentation ID, additional metric
  IDs, and canonically derived contract keys. Contract verification reads emitted
  generated output instead of reconstructing ownership from generator source text,
  and proves that every remaining catalog kind appears in the checked artifact.
- Azure SDK instrumentation now uses the SDK's first-party `Azure.*`
  `ActivitySource` path. Bootstrap enables `Azure.Experimental.EnableActivitySource`;
  `Qyl.Sdk` subscribes the wildcard and normalizes the bounded qyl domain/name/error
  contract before export.
- The `Qyl.Sdk` → Hosting → core NuGet dependency chain now preserves build and
  analyzer assets. A clean consumer references only `Qyl.Sdk`, executes `AddQyl`,
  requires an emitted interceptor, and runs the same payload as managed code and
  NativeAOT.
- `Qyl.Sdk` registers the following version-pinned, environment-switchable telemetry paths:
  - `Microsoft.Extensions.AI` 10.8.0 traces and metrics through the application's
    explicit `UseOpenTelemetry()` chat-client wrapper;
  - `Microsoft.Agents.AI` 1.13.0 traces and metrics through the application's
    explicit `UseOpenTelemetry()` agent wrapper;
  - `Microsoft.Agents.AI.Workflows` 1.13.0 traces through the application's explicit
    `WithOpenTelemetry()` workflow hook;
  - `ModelContextProtocol` 1.4.1 automatic official client/server traces, verified
    on the exact path under both managed execution and strict NativeAOT; and
  - `CoreWCF.Http` 1.9.1 managed server traces from `CoreWCF.Primitives`.
- The new signal-specific IDs are `MICROSOFTEXTENSIONSAI`, `MICROSOFTAGENTSAI`,
  `MICROSOFTAGENTSAIWORKFLOWS`, and `MCP`; CoreWCF uses `WCFCORE`. Each follows the
  standard `OTEL_DOTNET_AUTO_{SIGNAL}_{ID}_INSTRUMENTATION_ENABLED` switch shape.
- MCP is traces-only in 8.0. Its metrics are deliberately not registered because the
  official instruments attach dynamic tool and resource names as dimensions, which
  conflicts with qyl's bounded-cardinality policy.
- Direct OpenAI SDK instrumentation, raw Anthropic SDK instrumentation,
  `Azure.AI.Inference`, Amazon Bedrock, and A2A are not claimed by 8.0.

## [6.0.0] - 2026-07-18

Intentional pre-consumer convergence release: the last cheap breaking window
before launch. No compatibility shims; no external consumers existed for 5.x.

### Changed

- **BREAKING:** the generated-code ABI is now isolated in the
  `Qyl.OpenTelemetry.AutoInstrumentation.GeneratedCode` namespace: the 21
  `QylIntercepted*` runtime helpers and `QylMetricMeters` moved there, are
  hidden from completion (`EditorBrowsable(Never)`), and are deliberately
  versioned — generated interceptors reference the `QylGeneratedCodeAbi.V6`
  anchor constant, which is renamed on every future ABI break so mismatched
  generator/runtime package pairs fail to compile instead of misbehaving.
- **BREAKING:** everything that is neither the documented user bootstrap API
  nor generated-code ABI is now internal: `QylSemanticAttributes`,
  `QylActivityNames`, `QylActivitySource`, `QylAutoInstrumentationIds`,
  `QylAutoInstrumentationOptions`, `QylDbClientMetrics`, `QylInstrumentation`,
  `QylInstrumentationDomains`, `QylMetricNames`, `QylNServiceBusMetrics`.
  First-party sibling packages consume them via `InternalsVisibleTo`; demos
  were rewritten to use only the public surface plus the generated semconv
  packages, proving the public API suffices for real consumers.
- **BREAKING:** generated code no longer references
  `QylAutoInstrumentationOptions` or `QylInstrumentationDomains`: the GraphQL
  document opt-in is enforced solely at its runtime control point
  (`QylSensitiveCapturePolicy.SetGraphQlDocument`), and external-logger domain
  values are emitted as literals (identical IL — consts inline).
- **BREAKING:** `InterceptorTarget` derives contract keys structurally from
  `TelemetrySignal` + `InstrumentationId`; freeform `"signals.*"` key strings
  are unrepresentable in the generator and rejected by the gate.
- Interceptor body descriptors emit themselves (abstract `Emit` member); the
  emitter's runtime-type switch is gone and body-type exhaustiveness is
  enforced by the compiler.
- The contract-invariants gate was rebuilt zero-based (evidence or deletion):
  every surviving check carries an executed mutation proof or a cited external
  contract; internal member-name substring pins were deleted, so internal
  renames no longer break the gate.
- Public API baselines collapsed: `PublicAPI.Shipped.txt` now records the
  final 6.0.0 surface (core: 333 → 157 entries, including the stale removed
  `QylSelfTelemetry` rows), `PublicAPI.Unshipped.txt` is empty.

### User-facing API after convergence

- `Qyl.OpenTelemetry.AutoInstrumentation.Hosting`: `Boot()`,
  `AddQylAutoInstrumentation(...)`, `QylAutoInstrumentationHostingOptions`.
- `Qyl.Sdk`: `AddQyl(...)`, `QylSdkOptions`.
- Core: `AddQylAspNetCoreInstrumentation()`, `QylAutoInstrumentationSignal`.
- DiagnosticListeners: the public listener/subscriber surface (unchanged).
- Configuration remains environment-variable driven per the coverage matrix.

## [5.0.0] - 2026-07-13

### Changed

- **BREAKING:** removed the Telemetry Capability Graph API that leaked through the 4.1.0 core
  package, along with its never-published `.Publishing` companion project. A library-wide catalog
  was not evidence of the telemetry emitted by a particular consumer binary.
- **BREAKING:** removed the development conformance listener and its hosting/options surface; it
  changed sampling and had no product consumer.
- **BREAKING:** stopped binding ASP.NET classic capture/redaction and SQLClient .NET Framework IL
  rewrite options that this managed .NET 10/NativeAOT implementation cannot apply.
- Removed the unused 60-row contract manifest that the analyzer emitted into every consumer and
  the receiver-pattern projection that only fed the deleted graph; repository verification now
  compares actual generator target keys directly with the owned YAML.
- Replaced the generic ADO.NET demo's hand-written command double with a real in-memory SQLite
  database invoked through the provider-neutral `DbConnection`/`DbCommand` surface.
- Replaced hand-shaped OTLP JSON and protobuf substring scanning with a loopback receiver that
  decodes `ExportTraceServiceRequest` using the official OpenTelemetry protobuf package.

## [4.1.0] - 2026-07-13

### Changed

- Deps: `Qyl.OpenTelemetry.SemanticConventions` (+`.Incubating`) bumped 3.0.2 → 3.4.0 (registry
  1.41-era → **1.43.0**), restoring the workspace-wide version lockstep. Six `db.system.name`
  well-known values (`Elasticsearch`, `Mongodb`, `OracleDb`, `OtherSql`, `Redis`, `Sqlite`)
  repointed to the `.Incubating` assembly — semconv 3.3.0 removed the non-registry-stable enum
  members from the stable assembly. Wire values are unchanged; no emitted telemetry differs.
- **BREAKING (generator internals):** structural descriptor invariants — the self-referential
  validation apparatus is deleted (#28).
- Build: 30-demo matrix split into `Demos.slnx` (#36); SDK pinned to 10.0.301 with the MTP test
  runner.
- CI: all workflows on GitHub-hosted runners (#31); one live run per ref + job timeouts (#29);
  NativeAOT-publish warning-cleanliness gate (17 clean / 13 vendor-warned) (#27); aot-gate
  satisfies the `PublishAot` analyzer contract instead of redirecting artifacts (#30); real-demos
  on x64 — mssql/server ships no arm64 image (#32).
- Tools: local TCG-publishing verifier registered in the goal gate (#37); `artifacts/publish`
  dropped after passing verify gates (#35).
- Docs/chore: comment truth sweep (#33); agent-state purge (#34).

## [4.0.3] - 2026-07-01

### Fixed

- Semconv completeness: `url.scheme`, `http.request.method_original`, Azure operation
  (CODE RED #6/#7/#9) (#25).
- Build: `QylInstrumentation.Version` (the OTel instrumentation-scope version stamped on every
  emitted span/metric) is now baked from the build `<Version>` via a generated compile-time const
  instead of a hardcoded literal — no reflection. A new `verify-version-sync` gate keeps the props
  floor, the README examples, and that scope version aligned with the latest release tag (#21).

### Docs

- README pins the vocabulary to the official OpenTelemetry glossary (Instrumented Library /
  Instrumentation Library / Instrumentation Scope / Automatic Instrumentation / Semantic
  Conventions) and maps each term onto this repository, framing the AOT source-interceptor +
  `DiagnosticListener` mechanism as OpenTelemetry "Automatic Instrumentation". All three signals
  (traces, metrics, logs) are contract-covered; AOT-structural items are marked
  `unsupported_nativeaot`, not hidden (#19).
- Corrected the stale README install-example package version (`0.3.0-pre.1` → `4.0.0`) (#19).

## [4.0.2] - 2026-07-01

### Changed

- **BREAKING:** single-owner-per-signal registry — ends double instrumentation when multiple
  registration paths cover the same signal (CODE RED #3) (#23).

## [4.0.1] - 2026-07-01

### Fixed

- Real span duration, honest doc, and the correct thread-count instrument
  (CODE RED #1/#2/#10) (#22).

## [4.0.0] - 2026-07-01

### Changed

- **BREAKING:** dropped the `WebApplicationBuilder.Build()` interceptor. The ASP.NET Core
  server-request middleware is now registered via an `IStartupFilter` through
  `AddQylAspNetCoreInstrumentation()`, removing the cross-generator coordination layer and its
  opt-out property; middleware span semantics (request/response header + query-string capture) are
  preserved (#20).
- Build: added the `core.slnf` solution filter (core packages, no demo projects) (#18).

## [3.1.2] - 2026-06-30

### Fixed

- Generator no longer intercepts `RequestDelegate.Invoke`; delegate-invocation call-sites produced
  CS9207 (#17).

## [3.1.1] - 2026-06-30

### Changed

- Generator opts out of `WebApplicationBuilder.Build()` interception (#16).
- Generator: dropped dead `InterceptorTarget` mirrors and un-shadowed factory helpers (#15).

## [3.1.0] - 2026-06-29

### Added

- **Telemetry Capability Graph experiment.** `TelemetryCapabilityGraphGenerator` bakes the
  library contract into the core assembly as `QylTelemetryCapabilityGraph`. The repository also
  added an unpublished `Qyl.OpenTelemetry.AutoInstrumentation.Publishing` experiment and local
  demo; that package was not part of the five-package NuGet release (#12, #13, #14).

### Fixed

- Restored the green NativeAOT goal-gate floor and the slim-builder AOT demo (#10).

## [3.0.2] - 2026-06-24 — initial public release

First release under the canonical name, rebaselining the pre-public `0.x` line (the orphaned
`v0.*-pre` tags are skipped by the publish bump regex).

### Changed

- **Canonical rename** `Qyl.AutoInstrumentation` → `Qyl.OpenTelemetry.AutoInstrumentation`, with the
  package version aligned onto the Qyl.OpenTelemetry stack line at 3.0.2.
- Migrated to Central Package Management; onboarded Renovate with the shared preset.
- Split the Roslyn generator into detection / shapes / descriptors partials (no generated-output
  change).

### Added

- Apache-2.0 `LICENSE` (OpenTelemetry-ecosystem standard, incl. patent grant).
- Pure-managed, NativeAOT-ready runtime substrate: core APIs, source-generated
  `[InterceptsLocation]` interceptors, DiagnosticListener subscribers, `build/`/`buildTransitive/`
  assets, and `[ModuleInitializer]` activation. Hosting / EFCore / SqlClient packages isolate heavy
  dependencies and their AOT-warning boundaries.
- Real consumer demos (managed + NativeAOT where the library supports it): HttpClient, ASP.NET Core,
  EFCore, gRPC, and Microsoft.Data.SqlClient, plus Confluent.Kafka, RabbitMQ.Client, MongoDB.Driver,
  StackExchange.Redis, Quartz, and MassTransit against real Docker-backed brokers/servers.
  NServiceBus 9.2.11 is a documented managed-only boundary (endpoint creation requires
  Reflection.Emit).
- Behavioral verification gates: package layout, ProjectReference behavior, public API baselines,
  XML docs, environment options, conformance, source-generator snapshots, source-interceptor
  behavior, smoke, WebAPI AOT, and OTLP verified/collector fixtures.

### Telemetry semantics

- Adopted the upstream OpenTelemetry .NET privacy model and removed the
  `QYL_AUTOINSTRUMENTATION_CAPTURE_SENSITIVE_VALUES` option: `url.full` is always emitted on client
  spans and `url.path` / `url.query` on server spans, with query values redacted per key
  (`?token=Redacted`, keys stay); the `OTEL_DOTNET_EXPERIMENTAL_*_DISABLE_URL_QUERY_REDACTION` flags
  only switch redacted to raw. `db.namespace` is always emitted; `db.query.text` sits solely behind
  the upstream `SET_DBSTATEMENT_FOR_TEXT` flag. Bootstrap sets
  `System.Net.Http.DisableUriRedaction` so the BCL does not collapse query strings to `*` before qyl
  redacts per value.
- Added the upstream `OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES` option on the source-generated
  `MeterProviderBuilder.AddMeter(...)` path (exact meter-name case preserved, de-duplicated against
  built-ins), and expanded built-in meter registration (ASP.NET Core framework meters,
  `System.Net.NameResolution` for HTTP client, and the NServiceBus incoming-pipeline meter).
- Span names are OTel-semconv-shaped low-cardinality values composed by `QylActivityNames`
  (`{method}` / `{method} {route}` / `{rpc.service}/{rpc.method}` / `DB {operation}`), not fixed
  literals.
