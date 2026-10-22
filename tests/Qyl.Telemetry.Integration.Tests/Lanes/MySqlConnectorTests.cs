using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// MySqlConnector's native source against the testbed MySQL, managed and NativeAOT. Unlike Npgsql it
/// traces every Open, pooled or not; closing the connection is what it does not trace. MySqlConnector
/// picks its attribute flavour from OTEL_SEMCONV_STABILITY_OPT_IN, and the README documents qyl against
/// the stable one; the demo sets it too, so neither side can drift silently. 1054 is ER_BAD_FIELD_ERROR,
/// which the failed span carries in error.type and db.response.status_code.
/// </summary>
[TestClass]
[TestCategory("mysqlconnector")]
sealed class MySqlConnectorTests : SpanDemoLaneTests<MySqlConnectorTests>, ISpanDemoLane
{
    private const string ConnectionOpen = "connection-state=Open";
    private const string SelectScalar = "select-scalar=1";
    private const string ExpectedErrorNumber = "expected-mysql-error-number=1054";
    private const string ExperimentalPrefix = "Experimental.";

    private static readonly (string, string) StabilityOptIn = ("OTEL_SEMCONV_STABILITY_OPT_IN", "database");

    private static readonly Dictionary<string, string[]> Operations = new(StringComparer.Ordinal)
    {
        ["connection-open"] = ["MySqlConnector|Client"],
        ["select"] = ["MySqlConnector|Client"],
        ["failing-select"] = ["MySqlConnector|Client"],
        ["connection-close"] = [],
        ["pooled-reopen"] = ["MySqlConnector|Client"],
    };

    private static readonly DemoVariant[] Variants =
        [DemoVariant.Managed(DemoRuns.Managed, StabilityOptIn), DemoVariant.NativeAot(DemoRuns.NativeAot, StabilityOptIn)];

    public static string LaneName => "mysqlconnector";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, ConnectionOpen, SelectScalar, ExpectedErrorNumber);

    public static IEnumerable<object[]> SpanSetSides => Rows(RunKeys, LaneReport.Expected, LaneReport.Actual);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(OutcomeTokens))]
    public void DemoOutput_EachRun_PrintsOutcomeToken(string run, string token) =>
        Assert.Contains(token, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ExcludedSourcePrefixes_EachRun_AreOnlyExperimental(string run) =>
        Assert.AreSequenceEqual([ExperimentalPrefix], Run(run).Strings("ExcludedSourcePrefixes"));

    [TestMethod]
    [DynamicData(nameof(SpanSetSides))]
    public void SpanSets_EachRunAndSide_MatchThePinnedSpansPerOperation(string run, string side) =>
        Assert.AreSequenceEqual(LaneReport.SpanSets(Operations), LaneReport.SpanSets(Run(run), "Operations", "Operation", side), SequenceOrder.InAnyOrder);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsOneActivityPerPinnedSpan(string run) =>
        Assert.HasCount(Operations.Values.Sum(static spans => spans.Length), Run(run).Array("Activities"));
}
