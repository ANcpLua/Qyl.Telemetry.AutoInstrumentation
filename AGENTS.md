# Qyl.Telemetry.AutoInstrumentation

.NET instrumentation and hosting. HTTP/gRPC fixtures use Qyl.Telemetry.Tests.Capture in the pinned eng/dependencies/Qyl.Telemetry.Tests submodule; eng/build.sh initializes it. contracts/otel-dotnet-auto.upstream.yaml compares coverage, not runtime dependencies. Collector persistence is tested in qyl. Databases/brokers use eng/testbed and .railway/railway.ts. README documents commands; SKILL.md covers releases and consumers.
