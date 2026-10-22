using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>Confluent.Kafka's producer and consumer interceptors against the testbed Kafka (SASL/PLAIN), managed.</summary>
[TestClass]
[TestCategory("kafka")]
sealed class KafkaTests : SpanDemoLaneTests<KafkaTests>, ISpanDemoLane
{
    private const string ProducedOffset = "produced-offset=";
    private const string ConsumedMessages = "consumed-messages=2";
    private const string ExpectedError = "expected-kafka-error=";
    private const int ReportedActivities = 5;

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed)];

    public static string LaneName => "kafka";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, ProducedOffset, ConsumedMessages, ExpectedError);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(OutcomeTokens))]
    public void DemoOutput_EachRun_PrintsOutcomeToken(string run, string token) =>
        Assert.Contains(token, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsAtLeastFiveActivities(string run) =>
        Assert.IsGreaterThanOrEqualTo(ReportedActivities, Run(run).Array("Activities").Length);
}
