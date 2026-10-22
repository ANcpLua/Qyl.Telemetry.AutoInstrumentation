using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// ASP.NET Core held to the contract's own conformance signal: exactly one SERVER span per request,
/// the framework's own (Microsoft.AspNetCore), named after its route and carrying every attribute of
/// the aspnetcore.server signal. The attribute list is read from contracts/qyl-aot-ownership.yaml
/// rather than retyped, so adding one to the contract without emitting it fails this lane. The four
/// requests are one 204, one 500, one 404 that resolves no endpoint and one whose client disconnects
/// before anything is sent (499). http.route is the one conditionally required attribute: a request
/// that resolved no endpoint has no template to carry, and its name still has to come from qyl.
/// </summary>
[TestClass]
[TestCategory("aspnetcore")]
sealed class AspNetCoreTests : SpanDemoLaneTests<AspNetCoreTests>, ISpanDemoLane
{
    private const string ManagedCaptureOptIn = "managed-capture-opt-in";
    private const string NativeAotCaptureOptIn = "nativeaot-capture-opt-in";
    private const string ContractKey = "signals.traces.ASPNETCORE";
    private const string ConformanceSignal = "aspnetcore.server";
    private const string AspNetCoreSource = "Microsoft.AspNetCore";
    private const string MethodKey = "http.request.method";
    private const string RouteKey = "http.route";
    private const int ExpectedServerSpans = 4;

    private static readonly (string, string)[] CaptureOptIn =
    [
        ("OTEL_DOTNET_AUTO_TRACES_ASPNETCORE_INSTRUMENTATION_CAPTURE_REQUEST_HEADERS", "X-Demo-Req"),
        ("OTEL_DOTNET_AUTO_TRACES_ASPNETCORE_INSTRUMENTATION_CAPTURE_RESPONSE_HEADERS", "X-Demo-Res"),
        ("OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION", "true"),
    ];

    private static readonly DemoVariant[] Variants =
    [
        DemoVariant.Managed(DemoRuns.Managed),
        DemoVariant.Managed(ManagedCaptureOptIn, CaptureOptIn),
        DemoVariant.NativeAot(DemoRuns.NativeAot),
        DemoVariant.NativeAot(NativeAotCaptureOptIn, CaptureOptIn),
    ];

    private static readonly Lazy<IReadOnlyList<string>> RequiredAttributes = new(LoadRequiredAttributes);

    public static string LaneName => "aspnetcore";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants, "--no-incremental");

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsAtLeastOneActivityPerRequest(string run) =>
        Assert.IsGreaterThanOrEqualTo(ExpectedServerSpans, Run(run).Array("Activities").Length);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ServerSpans_EachRun_AreExactlyOnePerRequest(string run) =>
        Assert.HasCount(ExpectedServerSpans, ServerSpans(run), string.Join("\n", ServerSpans(run)));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ServerSpans_EachRun_ComeFromAspNetCoreItself(string run) =>
        Assert.AreSequenceEqual(ServerSpans(run).Select(static _ => AspNetCoreSource),
            ServerSpans(run).Select(static span => ReportedActivity.Text(span, "Source")));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ServerSpans_EachRun_CarryATagObject(string run) =>
        Assert.DoesNotContain(static span => !span.TryGetProperty("Tags", out var tags) || tags.ValueKind is not JsonValueKind.Object, ServerSpans(run));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ServerSpans_EachRun_CarryEveryRequiredAttribute(string run)
    {
        var missing = ServerSpans(run)
            .SelectMany(static span => RequiredFor(span).Where(key => !ReportedActivity.HasTag(span, key))
                .Select(key => $"{ReportedActivity.Text(span, "Name")}: {key}"))
            .ToArray();

        Assert.IsEmpty(missing, string.Join("\n", missing));
    }

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ServerSpans_EachRun_AreNamedAfterTheirRoute(string run) =>
        Assert.AreSequenceEqual(ServerSpans(run).Select(static span => RouteName(span)),
            ServerSpans(run).Select(static span => ReportedActivity.Text(span, "Name")));

    private static JsonElement[] ServerSpans(string run) =>
        [.. Run(run).Array("Activities").Where(static activity => ReportedActivity.Text(activity, "Kind") == "Server")];

    private static IEnumerable<string> RequiredFor(JsonElement span) =>
        string.IsNullOrEmpty(ReportedActivity.Tag(span, RouteKey))
            ? RequiredAttributes.Value.Where(static key => key != RouteKey)
            : RequiredAttributes.Value;

    private static string? RouteName(JsonElement span)
    {
        var method = ReportedActivity.Tag(span, MethodKey);
        var route = ReportedActivity.Tag(span, RouteKey);
        return string.IsNullOrEmpty(route) ? method : $"{method} {route}";
    }

    private static IReadOnlyList<string> LoadRequiredAttributes()
    {
        var ownership = Yaml.LoadMapping(Repo.ContractsDirectory / "qyl-aot-ownership.yaml");
        foreach (var item in ownership["ownership_items"] as List<object?> ?? [])
        {
            if (item is not OrderedDictionary<string, object?> entry || entry.GetValueOrDefault("key") as string != ContractKey)
                continue;
            foreach (var signal in entry.GetValueOrDefault("conformance_signals") as List<object?> ?? [])
            {
                if (signal is OrderedDictionary<string, object?> conformance && conformance.GetValueOrDefault("name") as string == ConformanceSignal)
                    return [.. ((List<object?>)conformance["required_attributes"]!).Cast<string>()];
            }
        }

        throw new InvalidDataException($"contracts/qyl-aot-ownership.yaml declares no {ConformanceSignal} conformance signal on {ContractKey}");
    }
}
