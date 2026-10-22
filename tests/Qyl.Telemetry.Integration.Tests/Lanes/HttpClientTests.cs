using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// One HttpClient.GetAsync produces exactly one non-Experimental span: the BCL's own client span. An
/// error status and a refused connection, managed and NativeAOT, with the BCL's duration metric; the
/// refused connection runs inside the application's own Spans.Work span, which carries the
/// RecordFailure triple.
/// </summary>
[TestClass]
[TestCategory("http-client")]
sealed class HttpClientTests : SpanDemoLaneTests<HttpClientTests>, ISpanDemoLane
{
    private const string ErrorStatus = "error-status=503";
    private const string ExpectedFailure = "expected-failure=HttpRequestException";
    private const string ExperimentalPrefix = "Experimental.";
    private const int ReportedActivities = 3;
    private const int ReportedMetricSeries = 2;

    private static readonly Dictionary<string, string[]> SpanSets = new(StringComparer.Ordinal)
    {
        ["error-status"] = ["System.Net.Http/Client"],
        ["connection-failure"] = ["Qyl.Telemetry.Application/Internal", "System.Net.Http/Client"],
    };

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed), DemoVariant.NativeAot(DemoRuns.NativeAot)];

    public static string LaneName => "http-client";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, ErrorStatus, ExpectedFailure);

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
    public void DemoReport_EachRun_ReportsThreeActivities(string run) =>
        Assert.HasCount(ReportedActivities, Run(run).Array("Activities"));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsTwoDurationMetricSeries(string run) =>
        Assert.HasCount(ReportedMetricSeries, Run(run).Array("Metrics"));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ReportedMetrics_EachRun_ReachTheCapture(string run)
    {
        var unmatched = CapturedTelemetry.UnmatchedMetrics(Run(run), Lane.ServiceName, LaneReport.Metrics(Run(run).Array("Metrics")));

        Assert.IsEmpty(unmatched, string.Join("\n", unmatched));
    }
}
