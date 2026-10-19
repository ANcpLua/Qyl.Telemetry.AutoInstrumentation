using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Qyl;
using Qyl.Telemetry.TestFixtures;
using Qyl.Telemetry.Tests.Capture;
using static Qyl.Telemetry.Tests.Capture.CaptureAssert;
using Qyl.Telemetry.SemanticConventions.Names;
using StatusCode = Grpc.Core.StatusCode;

var scenario = ClientCaptureScenario.Parse(args);
var profile = scenario.Profile;
CaptureAssert.NativeAot();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var serverBuilder = WebApplication.CreateSlimBuilder();
serverBuilder.Logging.ClearProviders();
serverBuilder.WebHost.ConfigureKestrel(static options => options.Listen(IPAddress.Loopback, 0,
    static listen => listen.Protocols = HttpProtocols.Http2));
await using var server = serverBuilder.Build();
var cancellationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
server.Run(async context =>
{
    context.Response.ContentType = "application/grpc";
    context.Response.Headers["x-demo-res-md"] = "sv1";
    context.Response.Headers["x-overlap"] = "from-header";
    context.Response.Headers["trace-bin"] = "YWJj";
    if (context.Request.Path == "/qyl.Capture/Cancel")
    {
        await context.Response.Body.WriteAsync(new byte[5], context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        cancellationStarted.SetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
    }
    context.Response.AppendTrailer("grpc-status", "0");
    context.Response.AppendTrailer("x-demo-trailer-md", "tv1");
    context.Response.AppendTrailer("x-overlap", "from-trailer");
    context.Response.AppendTrailer("trace-bin", "AAH/");
    // A zero-length protobuf message, framed by gRPC. The application consumes the stream naturally.
    await context.Response.Body.WriteAsync(new byte[5], context.RequestAborted);
    if (context.Request.Path == "/qyl.Capture/Stream")
        await context.Response.Body.WriteAsync(new byte[5], context.RequestAborted);
});
await server.StartAsync(timeout.Token);

var exporter = new ActivitySnapshotExporter(QylTelemetryNames.VendorActivitySources.GrpcNetClient, "System.Net.Http");
var builder = Host.CreateApplicationBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.AddQyl(options =>
{
    options.EnableCollectorDiscovery = false;
    options.RequireConfiguredEndpoint = true;
    options.EnableLogExport = false;
    options.EnableMetricsExport = false;
    options.AdditionalSources.Add("MetadataCapture.Fixture");
    options.ConfigureTracing = tracing =>
    {
        if (scenario.Control is ClientCaptureControl.Unsampled) tracing.SetSampler(new AlwaysOffSampler());
        tracing.AddProcessor(new BatchActivityExportProcessor(exporter));
    };
});
var marshaller = new Marshaller<byte[]>(static bytes => bytes, static bytes => bytes);
using (var host = builder.Build())
{
    await host.StartAsync(timeout.Token);
    using var source = new ActivitySource("MetadataCapture.Fixture");
    using var parent = source.CreateActivity("capture-parent", ActivityKind.Internal)?.Start();
    var expectedParent = parent?.Context;
    Require(!scenario.EmitsSpans || expectedParent.HasValue,
        "native gRPC capture requires a parent activity when client spans are expected");
    using (SuppressInstrumentationScope.Begin(scenario.Control is ClientCaptureControl.Suppressed))
    {
        using var channel = GrpcChannel.ForAddress(server.Urls.Single());
        var invoker = channel.CreateCallInvoker();
        var headers = RequestMetadata();
        using var unary = invoker.AsyncUnaryCall(Method("Unary", MethodType.Unary), null,
            new CallOptions(headers, cancellationToken: timeout.Token), []);
        Require((await unary.ResponseAsync).Length is 0, "unary result changed");
        using var stream = invoker.AsyncServerStreamingCall(Method("Stream", MethodType.ServerStreaming), null,
            new CallOptions(headers, cancellationToken: timeout.Token), []);
        var count = 0;
        while (await stream.ResponseStream.MoveNext(timeout.Token)) count++;
        Require(count is 2, "stream consumption changed");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var cancelled = invoker.AsyncServerStreamingCall(Method("Cancel", MethodType.ServerStreaming), null,
            new CallOptions(headers, cancellationToken: cancel.Token), []);
        await cancelled.ResponseHeadersAsync;
        Require(await cancelled.ResponseStream.MoveNext(timeout.Token), "cancellation stream did not start");
        await cancellationStarted.Task.WaitAsync(timeout.Token);
        cancel.Cancel();
        await ExpectFailure(cancelled.ResponseStream.MoveNext(timeout.Token), StatusCode.Cancelled);
        using var unavailableChannel = GrpcChannel.ForAddress("http://127.0.0.1:1");
        using var unavailable = unavailableChannel.CreateCallInvoker().AsyncUnaryCall(Method("Unavailable", MethodType.Unary), null,
            new CallOptions(headers, cancellationToken: timeout.Token), []);
        await ExpectFailure(unavailable.ResponseAsync, StatusCode.Unavailable);
    }
    Require(parent?.TagObjects.Any(static tag => tag.Key.StartsWith("rpc.request.metadata.", StringComparison.Ordinal) ||
        tag.Key.StartsWith("rpc.response.metadata.", StringComparison.Ordinal)) != true, "parent decorated");
    Require(host.Services.GetRequiredService<TracerProvider>().ForceFlush(), "flush failed");
    var snapshots = exporter.GetSnapshots();
    CaptureAssert.UniqueSpans(snapshots);
    var grpc = snapshots.Where(static span => span.Source == QylTelemetryNames.VendorActivitySources.GrpcNetClient).ToArray();
    var active = scenario.EmitsSpans;
    string[] expectedMethods = active
        ? ["/qyl.Capture/Cancel", "/qyl.Capture/Stream", "/qyl.Capture/Unary", "/qyl.Capture/Unavailable"]
        : [];
    var actualMethods = grpc.Select(static span => (string)span.Tags["grpc.method"]!)
        .Order(StringComparer.Ordinal).ToArray();
    Require(actualMethods.SequenceEqual(expectedMethods),
        $"native gRPC operations changed: expected [{string.Join(", ", expectedMethods)}], actual [{string.Join(", ", actualMethods)}]");
    foreach (var span in snapshots.Where(static span => span.Source != QylTelemetryNames.VendorActivitySources.GrpcNetClient))
        Require(!span.Tags.Keys.Any(static key => key.StartsWith("rpc.request.metadata.", StringComparison.Ordinal) ||
            key.StartsWith("rpc.response.metadata.", StringComparison.Ordinal)), "RPC metadata decorated HTTP child");
    foreach (var span in grpc)
    {
        Require(span is { Operation: "Grpc.Net.Client.GrpcOut", Kind: ActivityKind.Client, Status: ActivityStatusCode.Unset }, "native ownership/status changed");
        CaptureAssert.Parent(span, expectedParent!.Value);
        var method = (string)span.Tags["grpc.method"]!;
        var status = Convert.ToInt32(span.Tags["grpc.status_code"], System.Globalization.CultureInfo.InvariantCulture);
        Require(status == (method.EndsWith("Cancel", StringComparison.Ordinal) ? 1 : method.EndsWith("Unavailable", StringComparison.Ordinal) ? 14 : 0), "native status tag changed");
        var requestCaptured = scenario.RequestCaptured;
        var responseCaptured = scenario.ResponseCaptured;
        if (requestCaptured && profile != ClientCaptureProfiles.BinaryOnly)
        {
            CaptureAssert.Values(span.Tags, "rpc.request.metadata.x-demo-md", ["mv1"]);
            CaptureAssert.Values(span.Tags, "rpc.request.metadata.x-request-id", ["dash"]);
            CaptureAssert.Values(span.Tags, "rpc.request.metadata.x_request_id", ["underscore"]);
            CaptureAssert.Values(span.Tags, "rpc.request.metadata.x.request.id", ["dot"]);
            CaptureAssert.Values(span.Tags, "rpc.request.metadata.x-multi", ["first", "first", "", "comma,kept"]);
        }
        else if (!requestCaptured) Require(!span.Tags.Keys.Any(static key => key.StartsWith("rpc.request.metadata.", StringComparison.Ordinal)), "request not opt-in");
        if (responseCaptured && profile != ClientCaptureProfiles.BinaryOnly && !method.EndsWith("Unavailable", StringComparison.Ordinal))
        {
            CaptureAssert.Values(span.Tags, "rpc.response.metadata.x-demo-res-md", ["sv1"]);
            if (method.EndsWith("Cancel", StringComparison.Ordinal))
            {
                CaptureAssert.Values(span.Tags, "rpc.response.metadata.x-overlap", ["from-header"]);
                Require(!span.Tags.ContainsKey("rpc.response.metadata.x-demo-trailer-md"), "invented cancellation trailer");
            }
            else
            {
                CaptureAssert.Values(span.Tags, "rpc.response.metadata.x-demo-trailer-md", ["tv1"]);
                CaptureAssert.Values(span.Tags, "rpc.response.metadata.x-overlap", ["from-header", "from-trailer"]);
            }
        }
        else if (!responseCaptured || method.EndsWith("Unavailable", StringComparison.Ordinal)) Require(!span.Tags.Keys.Any(static key => key.StartsWith("rpc.response.metadata.", StringComparison.Ordinal)), "invented or unselected response");
        if (requestCaptured) CaptureAssert.Values(span.Tags, "rpc.request.metadata.trace-bin", ["AQID"]);
        if (responseCaptured && !method.EndsWith("Unavailable", StringComparison.Ordinal))
            CaptureAssert.Values(span.Tags, "rpc.response.metadata.trace-bin", method.EndsWith("Cancel", StringComparison.Ordinal)
                ? ["YWJj"] : ["YWJj", "AAH/"]);
        Require(!span.Tags.ContainsKey("rpc.request.metadata.unselected"), "unselected metadata captured");
    }
    await host.StopAsync(timeout.Token);
}
// The native sources still emit when an independent listener is present, after qyl is disposed.
var afterDispose = new ConcurrentQueue<ActivitySnapshot>();
using (var observer = new ActivityListener())
{
    observer.ShouldListenTo = static source => source.Name is "Grpc.Net.Client" or "System.Net.Http";
    observer.Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded;
    observer.ActivityStopped = activity => afterDispose.Enqueue(ActivitySnapshot.Capture(activity));
    ActivitySource.AddActivityListener(observer);
    using var channel = GrpcChannel.ForAddress(server.Urls.Single());
    using var unary = channel.CreateCallInvoker().AsyncUnaryCall(Method("Unary", MethodType.Unary), null,
        new CallOptions(RequestMetadata(), cancellationToken: timeout.Token), []);
    Require((await unary.ResponseAsync).Length is 0, "post-disposal unary result changed");
}
CaptureAssert.AfterDisposal(afterDispose.ToArray(), QylTelemetryNames.VendorActivitySources.GrpcNetClient);
await server.StopAsync(timeout.Token);
Console.WriteLine(NativeCaptureReport.Serialize(profile, exporter.GetSnapshots(), afterDispose.ToArray()));

Method<byte[], byte[]> Method(string name, MethodType type) => new(type, "qyl.Capture", name, marshaller, marshaller);

static async Task ExpectFailure<T>(Task<T> response, StatusCode expected)
{
    var failed = false;
    try { await response; }
    catch (RpcException exception) when (exception.StatusCode == expected) { failed = true; }
    Require(failed, "call did not fail with expected native gRPC status");
}

static Metadata RequestMetadata() => new()
{
    { "x-demo-md", "mv1" }, { "x-request-id", "dash" }, { "x_request_id", "underscore" },
    { "x.request.id", "dot" }, { "x-multi", "first" }, { "x-multi", "first" },
    { "x-multi", "" }, { "x-multi", "comma,kept" }, { "trace-bin", [1, 2, 3] },
    { "unselected", "absent" }
};
