using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Qyl.Build.OtlpEvidence;

/// <summary>
/// The OTLP wire evidence. A README-style Qyl.Telemetry.Hosting package consumer makes one real
/// HttpClient request to a loopback downstream and exports to a loopback OTLP/HTTP receiver in this
/// process, which decodes the request bytes with the official OTLP messages.
/// <para>
/// This gate exists because the collector API cannot prove what it proves: the request path and
/// content type the exporter used, that the body is an ExportTraceServiceRequest, the 16- and 8-byte
/// trace and span ids, the integer type of an attribute, and — above all — that url.full left the
/// process with its query redacted. The qyl collector denies url.full at ingest, so a leaked secret
/// would be invisible through its API; only the wire shows it.
/// </para>
/// </summary>
static class OtlpWireEvidence
{
    private const string Fixture = "OtlpEvidence";
    private const string Secret = "super-secret";
    private const string LoopbackPrefix = "http://127.0.0.1:";

    private static AbsolutePath VerifiedPath => Repo.Fixtures / Fixture / "verified" / "trace-evidence.json";

    public static void Run(bool updateVerified)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            RunGate(updateVerified);
        }
        finally
        {
            Log.Information("otlp-receiver-evidence-elapsed={Elapsed}s", stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture));
        }
    }

    private static void RunGate(bool updateVerified)
    {
        var version = LocalFeed.EnsurePacked();
        var directory = FixtureWorkspace.Create("otlp-evidence-consumer");
        FixtureWorkspace.CopyFixture(Fixture, directory, "Program.cs");
        var project = directory / "Consumer.csproj";
        ConsumerFixture.WriteProject(project, "Microsoft.NET.Sdk",
            "    <OutputType>Exe</OutputType>\n",
            ConsumerFixture.PackageReference("Qyl.Telemetry.Hosting", version));
        DotNet.Build(project, workingDirectory: directory);

        JsonObject report;
        using (var otlp = LoopbackServer.Start(HandleOtlp))
        using (var downstream = LoopbackServer.Start(HandleDownstream))
        {
            var run = ConsumerFixture.RunAssembly(
                ConsumerFixture.Assembly(directory, "Consumer"),
                arguments: [$"{otlp.BaseUrl}v1/traces", $"{downstream.BaseUrl}probe?access_token={Secret}"]);
            Gate.Require(run.ExitCode == 0, () => "OTLP consumer failed\n" + run.Describe());
            report = BuildReport(otlp.Requests, downstream.Requests);
        }

        var rendered = CanonicalJson.Render(report.ToJsonString());
        if (updateVerified)
        {
            VerifiedPath.Parent.CreateDirectory();
            File.WriteAllText(VerifiedPath, rendered);
            Log.Information("updated {Verified}", Repo.Relative(VerifiedPath));
            return;
        }

        // The scope version was the package under test while the span was qyl's own. Since 15.0.0 it is
        // System.Net.Http's scope, whose version is empty and does not move with a qyl release, so the
        // recorded value stands as recorded.
        Gate.Require(VerifiedPath.FileExists(), $"missing OTLP verified evidence: {VerifiedPath}");
        var expected = CanonicalJson.Render(File.ReadAllText(VerifiedPath));
        Gate.Require(rendered == expected, () => $"typed OTLP receiver evidence mismatch\n{ConsumerFixture.Diff(expected, rendered)}\nexpected: {expected}\nactual: {rendered}");
        Log.Information("otlp-receiver-evidence-ok");
    }

    private sealed record OtlpRequest(string Method, string Path, string ContentType, ExportTraceServiceRequest Message);

    private sealed record InvalidOtlpRequest(string Path, string Error);

    private sealed record HttpRequest(string Method, string Path);

    private static object? HandleOtlp(HttpListenerContext context)
    {
        if (context.Request.HttpMethod != "POST")
        {
            context.Response.StatusCode = 501;
            return null;
        }

        using var body = new MemoryStream();
        context.Request.InputStream.CopyTo(body);
        ExportTraceServiceRequest message;
        try
        {
            message = ExportTraceServiceRequest.Parser.ParseFrom(body.ToArray());
        }
        catch (InvalidProtocolBufferException error)
        {
            context.Response.StatusCode = 400;
            return new InvalidOtlpRequest(context.Request.RawUrl ?? string.Empty, "exporter sent invalid OTLP protobuf: " + error.Message);
        }

        var response = new ExportTraceServiceResponse().ToByteArray();
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/x-protobuf";
        context.Response.ContentLength64 = response.Length;
        context.Response.OutputStream.Write(response);
        return new OtlpRequest("POST", context.Request.RawUrl ?? string.Empty, NormalizeContentType(context.Request.ContentType ?? string.Empty), message);
    }

    private static object? HandleDownstream(HttpListenerContext context)
    {
        if (context.Request.HttpMethod != "GET")
        {
            context.Response.StatusCode = 501;
            return null;
        }

        context.Response.StatusCode = 204;
        context.Response.ContentLength64 = 0;
        return new HttpRequest("GET", context.Request.RawUrl ?? string.Empty);
    }

    private static JsonObject BuildReport(IReadOnlyList<object> otlpRequests, IReadOnlyList<object> downstreamRequests)
    {
        var downstreamPath = $"/probe?access_token={Secret}";
        Gate.Require(downstreamRequests is [HttpRequest { Method: "GET" } only] && only.Path == downstreamPath,
            () => $"unexpected downstream requests: [{string.Join(", ", downstreamRequests)}]");
        var malformed = otlpRequests.OfType<InvalidOtlpRequest>().ToArray();
        Gate.Require(malformed.Length == 0, () => "invalid OTLP requests: " + string.Join("; ", malformed.Select(static request => $"{request.Path}: {request.Error}")));
        Gate.Require(otlpRequests is [OtlpRequest],
            () => $"expected one typed OTLP request, got [{string.Join(", ", otlpRequests)}]");

        var request = (OtlpRequest)otlpRequests[0];
        Gate.Require((request.Method, request.Path, request.ContentType) == ("POST", "/v1/traces", "application/x-protobuf"),
            $"unexpected OTLP request metadata: {request.Method} {request.Path} {request.ContentType}");

        var candidates = request.Message.ResourceSpans
            .SelectMany(static resourceSpans => resourceSpans.ScopeSpans)
            .SelectMany(static scopeSpans => scopeSpans.Spans.Select(span => (Scope: scopeSpans.Scope ?? new InstrumentationScope(), Span: span)))
            .ToArray();
        var matches = candidates.Where(static candidate => Equals(Attributes(candidate.Span).GetValueOrDefault("qyl.instrumentation.domain"), "http.client")).ToArray();
        Gate.Require(matches.Length == 1, $"expected exactly one qyl HTTP client span, got {matches.Length}");

        var (scope, span) = matches[0];
        var values = Attributes(span);
        var required = new (string Key, object Value)[]
        {
            ("qyl.instrumentation.domain", "http.client"),
            ("http.request.method", "GET"),
            ("http.response.status_code", 204L),
            ("server.address", "127.0.0.1"),
        };
        foreach (var (key, value) in required)
            Gate.Require(Equals(values.GetValueOrDefault(key), value), $"unexpected {key}: expected={value} actual={Show(values.GetValueOrDefault(key))}");

        // 15.0.0 handed the client lane to System.Net.Http, so the redaction is the BCL's: it replaces
        // the whole query with "*". What the gate proves is that the secret does not leave the process;
        // a consumer that sets System.Net.Http.DisableUriRedaction turns this off, which is why the
        // assertion is on the secret's absence rather than on the exact placeholder.
        var url = values.GetValueOrDefault("url.full") as string;
        Gate.Require(url is not null && !url.Contains(Secret, StringComparison.Ordinal) && url.Contains("?*", StringComparison.Ordinal),
            $"url.full was not safely redacted: {Show(url)}");
        var (normalizedUrl, urlPort) = NormalizeLoopbackUrl(url!);
        Gate.Require(values.GetValueOrDefault("server.port") is long serverPort and > 0 && serverPort == urlPort,
            $"server.port did not match the real loopback URL: attribute={Show(values.GetValueOrDefault("server.port"))} url={url}");
        Gate.Require(!values.Values.Any(static value => Show(value).Contains(Secret, StringComparison.Ordinal)), "OTLP attributes leaked the downstream secret");
        Gate.Require(span.TraceId.Length == 16 && span.SpanId.Length == 8, "OTLP span carries invalid trace/span identifiers");
        Gate.Require(span.StartTimeUnixNano > 0 && span.EndTimeUnixNano >= span.StartTimeUnixNano, "OTLP span carries invalid timestamps");
        // The scope is the emitting library's: System.Net.Http since 15.0.0. What is still qyl's is the
        // domain attribute asserted above.
        Gate.Require(scope.Name == "System.Net.Http",
            $"OTLP instrumentation scope did not match the package under test: name={scope.Name} version={scope.Version}");

        var attributes = new JsonObject();
        foreach (var (key, value) in required)
            attributes[key] = value is long number ? JsonValue.Create(number) : JsonValue.Create((string)value);
        attributes["server.port"] = "<port>";
        attributes["url.full"] = normalizedUrl;

        return new JsonObject
        {
            ["downstream"] = new JsonObject { ["method"] = "GET", ["path"] = downstreamPath },
            ["otlp"] = new JsonObject
            {
                ["contentType"] = request.ContentType,
                ["method"] = request.Method,
                ["path"] = request.Path,
                ["wireType"] = ExportTraceServiceRequest.Descriptor.Name,
            },
            ["span"] = new JsonObject
            {
                ["attributes"] = attributes,
                ["idBytes"] = new JsonObject { ["span"] = span.SpanId.Length, ["trace"] = span.TraceId.Length },
                ["kind"] = Span.Descriptor.EnumTypes.Single(static type => type.Name == "SpanKind").FindValueByNumber((int)span.Kind).Name,
                ["name"] = span.Name,
                ["scope"] = new JsonObject { ["name"] = scope.Name, ["version"] = scope.Version },
                ["timestampsValid"] = true,
            },
        };
    }

    /// <summary>The span's attributes with their wire types: a string stays a string, an int is a long.</summary>
    private static Dictionary<string, object?> Attributes(Span span) =>
        span.Attributes.ToDictionary(static attribute => attribute.Key, static attribute => Value(attribute.Value), StringComparer.Ordinal);

    private static object? Value(AnyValue? value) => value?.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.IntValue => value.IntValue,
        AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
        AnyValue.ValueOneofCase.BytesValue => value.BytesValue,
        AnyValue.ValueOneofCase.ArrayValue => value.ArrayValue,
        AnyValue.ValueOneofCase.KvlistValue => value.KvlistValue,
        _ => null,
    };

    private static string Show(object? value) => value switch
    {
        null => "None",
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static (string Normalized, int Port) NormalizeLoopbackUrl(string value)
    {
        Gate.Require(value.StartsWith(LoopbackPrefix, StringComparison.Ordinal), $"expected loopback url.full, got {value}");
        var rest = value[LoopbackPrefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        Gate.Require(slash > 0 && rest[..slash].All(char.IsAsciiDigit), $"expected numeric loopback port in url.full, got {value}");
        var port = int.Parse(rest[..slash], CultureInfo.InvariantCulture);
        Gate.Require(port is > 0 and <= 65535, $"loopback port is outside the TCP range: {port}");
        return (LoopbackPrefix + "<port>/" + rest[(slash + 1)..], port);
    }

    private static string NormalizeContentType(string contentType) => contentType.Split(';', 2)[0].Trim().ToLowerInvariant();

    /// <summary>A loopback HTTP server that records what each request's handler returns.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Func<HttpListenerContext, object?> _handler;
        private readonly List<object> _requests = [];
        private readonly Task _loop;

        private LoopbackServer(HttpListener listener, string baseUrl, Func<HttpListenerContext, object?> handler)
        {
            _listener = listener;
            _handler = handler;
            BaseUrl = baseUrl;
            _loop = Task.Run(Serve);
        }

        public string BaseUrl { get; }

        public IReadOnlyList<object> Requests
        {
            get
            {
                lock (_requests)
                    return [.. _requests];
            }
        }

        public static LoopbackServer Start(Func<HttpListenerContext, object?> handler)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var baseUrl = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(baseUrl);
            listener.Start();
            return new LoopbackServer(listener, baseUrl, handler);
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                try
                {
                    var recorded = _handler(context);
                    if (recorded is not null)
                    {
                        lock (_requests)
                            _requests.Add(recorded);
                    }
                }
                finally
                {
                    context.Response.Close();
                }
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
