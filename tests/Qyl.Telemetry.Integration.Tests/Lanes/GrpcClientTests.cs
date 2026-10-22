using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>Grpc.Net.Client's native source: the raw CallInvoker shape, the ClientBase shape, and the
/// unconfigured metadata contract, each managed and NativeAOT. ClientBase&lt;T&gt; and the raw CallInvoker
/// share one native owner; metadata is opt-in in both.</summary>
[TestClass]
[TestCategory("grpc-client")]
sealed class GrpcClientTests : SpanDemoLaneTests<GrpcClientTests>, ISpanDemoLane
{
    private const string ManagedClient = "managed-client";
    private const string NativeAotClient = "nativeaot-client";
    private const string ExpectedFailure = "expected-failure=Unavailable";
    private const string GrpcDomain = "rpc.grpc";
    private const int GrpcClientActivities = 2;

    private static readonly (string, string) ClientMode = ("QYL_GRPC_DEMO_MODE", "client");

    private static readonly DemoVariant[] Variants =
    [
        DemoVariant.Managed(DemoRuns.Managed),
        DemoVariant.NativeAot(DemoRuns.NativeAot),
        DemoVariant.Managed(ManagedClient, ClientMode),
        DemoVariant.NativeAot(NativeAotClient, ClientMode),
    ];

    public static string LaneName => "grpc-client";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, ExpectedFailure);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(OutcomeTokens))]
    public void DemoOutput_EachRun_PrintsOutcomeToken(string run, string token) =>
        Assert.Contains(token, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsTwoGrpcClientActivities(string run) =>
        Assert.HasCount(GrpcClientActivities,
            Run(run).Array("Activities").Where(static activity => ReportedActivity.Tag(activity, "qyl.instrumentation.domain") == GrpcDomain));
}
