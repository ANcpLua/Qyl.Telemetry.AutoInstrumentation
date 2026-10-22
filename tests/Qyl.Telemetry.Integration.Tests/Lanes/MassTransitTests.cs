using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// MassTransit's own ActivitySource over the testbed RabbitMQ: one native span per command, the
/// publish and the explicit send. A third would be the deleted interceptor emitting alongside it.
/// Managed and NativeAOT (a vendor-warned publish).
/// </summary>
[TestClass]
[TestCategory("masstransit")]
sealed class MassTransitTests : SpanDemoLaneTests<MassTransitTests>, ISpanDemoLane
{
    private const string Published = "published=alpha";
    private const string Sent = "sent=beta";
    private const int ReportedActivities = 2;

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed), DemoVariant.NativeAot(DemoRuns.NativeAot)];

    public static string LaneName => "masstransit";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, Published, Sent);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(OutcomeTokens))]
    public void DemoOutput_EachRun_PrintsOutcomeToken(string run, string token) =>
        Assert.Contains(token, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsTwoActivities(string run) =>
        Assert.HasCount(ReportedActivities, Run(run).Array("Activities"));
}
