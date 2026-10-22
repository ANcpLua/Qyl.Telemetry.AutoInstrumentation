using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>RabbitMQ.Client's publisher source against the testbed RabbitMQ, managed and NativeAOT.</summary>
[TestClass]
[TestCategory("rabbitmq")]
sealed class RabbitMqTests : SpanDemoLaneTests<RabbitMqTests>, ISpanDemoLane
{
    private const string PublishedQueue = "published-queue=";
    private const string PublishedExchange = "published-exchange=qyl-probe-exchange";
    private const int ReportedActivities = 2;

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed), DemoVariant.NativeAot(DemoRuns.NativeAot)];

    public static string LaneName => "rabbitmq";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, PublishedQueue, PublishedExchange);

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
