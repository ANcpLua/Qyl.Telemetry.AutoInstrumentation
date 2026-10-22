using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// MongoDB.Driver's native source against the testbed MongoDB, managed: three collection commands,
/// each traced in two layers (an operation span and the command span beneath it). The driver's
/// handshake and topology commands name no collection and are filtered out by the demo.
/// </summary>
[TestClass]
[TestCategory("mongodb")]
sealed class MongoDbTests : SpanDemoLaneTests<MongoDbTests>, ISpanDemoLane
{
    private const string Inserted = "inserted-documents=1";
    private const string Found = "found-documents=1";
    private const string Deleted = "deleted-documents=1";
    private const int ReportedActivities = 6;

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed)];

    public static string LaneName => "mongodb";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, Inserted, Found, Deleted);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(OutcomeTokens))]
    public void DemoOutput_EachRun_PrintsOutcomeToken(string run, string token) =>
        Assert.Contains(token, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsSixActivities(string run) =>
        Assert.HasCount(ReportedActivities, Run(run).Array("Activities"));
}
