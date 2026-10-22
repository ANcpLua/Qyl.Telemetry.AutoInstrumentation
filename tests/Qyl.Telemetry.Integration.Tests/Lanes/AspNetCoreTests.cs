using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// ASP.NET Core held to the contract's own conformance signal: exactly one SERVER span per request,
/// the framework's own (Microsoft.AspNetCore), named after its route and carrying every attribute of
/// the aspnetcore.server signal. The attribute list is read from contracts/qyl-aot-ownership.yaml
/// rather than retyped, so adding one to the contract without emitting it fails this lane.
/// </summary>
[TestClass]
[TestCategory("aspnetcore")]
public sealed class AspNetCoreTests(TestContext testContext)
{
    private const string ContractKey = "signals.traces.ASPNETCORE";
    private const string ConformanceSignal = "aspnetcore.server";
    private const string AspNetCoreSource = "Microsoft.AspNetCore";
    private const string MethodKey = "http.request.method";
    private const string RouteKey = "http.route";

    // One 204 request, one 500 request, one 404 that resolves no endpoint and one request whose
    // client disconnects before anything is sent (499).
    private const int ExpectedServerSpans = 4;

    [TestMethod]
    public async Task ExportsExpectedTelemetry()
    {
        var lane = new DemoContext(DemoLanes.ByName("aspnetcore"), testContext);
        var optIn = lane.With(
            ("OTEL_DOTNET_AUTO_TRACES_ASPNETCORE_INSTRUMENTATION_CAPTURE_REQUEST_HEADERS", "X-Demo-Req"),
            ("OTEL_DOTNET_AUTO_TRACES_ASPNETCORE_INSTRUMENTATION_CAPTURE_RESPONSE_HEADERS", "X-Demo-Res"),
            ("OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION", "true"));
        var required = RequiredAttributes();

        lane.BuildManaged("--no-incremental");
        Verify(lane, (await lane.RunManagedAsync("managed ASP.NET Core demo")), required);
        Verify(lane, (await lane.RunManagedAsync("managed ASP.NET Core demo (capture opt-in)", optIn)), required);
        var executable = lane.PublishNativeAot();
        Verify(lane, (await lane.RunNativeAotAsync(executable, "NativeAOT ASP.NET Core demo")), required);
        Verify(lane, (await lane.RunNativeAotAsync(executable, "NativeAOT ASP.NET Core demo (capture opt-in)", optIn)), required);
    }

    private static void Verify(DemoContext lane, DemoRun run, IReadOnlyList<string> required)
    {
        run.RequireSucceeded().RequireReportPassed();
        var activities = run.RequireAtLeast("Activities", ExpectedServerSpans, "ASP.NET Core activities");
        var servers = activities.EnumerateArray().Where(static activity => ReportedActivity.Text(activity, "Kind") == "Server").ToArray();
        Assert.HasCount(ExpectedServerSpans, servers, $"{run.Label} exported {servers.Length} server spans for {ExpectedServerSpans} requests — a second one means a qyl-made server span came back:\n{string.Join("\n", servers)}");

        foreach (var span in servers)
        {
            var name = ReportedActivity.Text(span, "Name");
            Assert.AreEqual(AspNetCoreSource, ReportedActivity.Text(span, "Source"),
                $"{run.Label} server span is not ASP.NET Core's own: source={ReportedActivity.Text(span, "Source")} name={name}");
            Assert.IsTrue(span.TryGetProperty("Tags", out var tags) && tags.ValueKind is JsonValueKind.Object, $"{run.Label} server span carries no tags: {span}");

            // http.route is the one conditionally required attribute: a request that resolved no
            // endpoint has no template to carry. The name still has to come from qyl.
            var route = ReportedActivity.Tag(span, RouteKey);
            var expected = string.IsNullOrEmpty(route) ? [.. required.Where(static key => key != RouteKey)] : required;
            var missing = expected.Where(key => !ReportedActivity.HasTag(span, key)).ToArray();
            Assert.IsEmpty(missing, $"{run.Label} server span '{name}' is missing [{string.Join(", ", missing)}]");

            var method = ReportedActivity.Tag(span, MethodKey);
            var expectedName = string.IsNullOrEmpty(route) ? method : $"{method} {route}";
            Assert.AreEqual(expectedName, name, $"{run.Label} server span is named '{name}', not after its route ('{expectedName}')");
        }

        lane.Telemetry.Spans(run, activities, "ASP.NET Core");
    }

    private static IReadOnlyList<string> RequiredAttributes()
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

        throw new AssertFailedException($"contracts/qyl-aot-ownership.yaml declares no {ConformanceSignal} conformance signal on {ContractKey}");
    }
}
