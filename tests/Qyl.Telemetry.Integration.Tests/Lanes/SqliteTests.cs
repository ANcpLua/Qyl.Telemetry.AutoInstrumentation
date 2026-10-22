using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>The ADO.NET interceptor's SQLITE fan-out against Microsoft.Data.Sqlite, managed and NativeAOT.</summary>
[TestClass]
[TestCategory("sqlite")]
sealed class SqliteTests : SpanDemoLaneTests<SqliteTests>, ISpanDemoLane
{
    private const string SqliteValue = "sqlite-value=alpha";
    private const string ExpectedError = "expected-sqlite-error=1";
    private const int ReportedActivities = 4;

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed), DemoVariant.NativeAot(DemoRuns.NativeAot)];

    public static string LaneName => "sqlite";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, SqliteValue, ExpectedError);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(OutcomeTokens))]
    public void DemoOutput_EachRun_PrintsOutcomeToken(string run, string token) =>
        Assert.Contains(token, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsFourActivities(string run) =>
        Assert.HasCount(ReportedActivities, Run(run).Array("Activities"));
}
