using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// GraphQL.NET's ActivitySource is silent until the application calls UseTelemetry() itself: the
/// opted-in executer produces exactly one span, the identical executer without the opt-in none.
/// Managed and NativeAOT (a vendor-warned publish).
/// </summary>
[TestClass]
[TestCategory("graphql")]
sealed class GraphQlTests : SpanDemoLaneTests<GraphQlTests>, ISpanDemoLane
{
    private const string WithTelemetrySucceeded = "graphql-with-telemetry-success=true";
    private const string WithoutTelemetrySucceeded = "graphql-without-telemetry-success=true";
    private const string ExperimentalPrefix = "Experimental.";
    private const int ReportedActivities = 1;

    private static readonly Dictionary<string, string[]> SpanSets = new(StringComparer.Ordinal)
    {
        ["query-with-telemetry"] = ["GraphQL/Internal"],
        ["query-without-telemetry"] = [],
    };

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed), DemoVariant.NativeAot(DemoRuns.NativeAot)];

    public static string LaneName => "graphql";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, WithTelemetrySucceeded, WithoutTelemetrySucceeded);

    public static IEnumerable<object[]> SpanSetSides => Rows(RunKeys, LaneReport.Expected, LaneReport.Actual);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(OutcomeTokens))]
    public void DemoOutput_EachRun_PrintsOutcomeToken(string run, string token) =>
        Assert.Contains(token, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(SpanSetSides))]
    public void SpanSets_EachRunAndSide_MatchThePinnedSpansPerOperation(string run, string side) =>
        Assert.AreSequenceEqual(LaneReport.SpanSets(SpanSets), LaneReport.SpanSets(Run(run), "SpanSets", "Operation", side), SequenceOrder.InAnyOrder);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ExcludedScopes_EachRun_AreOnlyExperimental(string run) =>
        Assert.IsEmpty(LaneReport.PrefixViolations(Run(run).Strings("ExcludedScopes"), ExperimentalPrefix));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsOneActivity(string run) =>
        Assert.HasCount(ReportedActivities, Run(run).Array("Activities"));
}
