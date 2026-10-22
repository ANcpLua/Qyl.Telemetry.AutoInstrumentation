using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// Npgsql's native source against the testbed PostgreSQL, managed and NativeAOT: the physical connect
/// and every command, and nothing else — returning a connection to the pool and taking it back out
/// are untraced. The failing select runs inside the application's own Spans.Work span, which carries
/// the RecordFailure triple. 42703 is the SQLSTATE for undefined_column, which is also what Npgsql
/// writes into error.type.
/// </summary>
[TestClass]
[TestCategory("npgsql")]
sealed class NpgsqlTests : SpanDemoLaneTests<NpgsqlTests>, ISpanDemoLane
{
    private const string ConnectionOpen = "connection-state=Open";
    private const string SelectScalar = "select-scalar=1";
    private const string ExpectedSqlState = "expected-postgres-sqlstate=42703";
    private const string ExperimentalPrefix = "Experimental.";

    private static readonly Dictionary<string, string[]> Operations = new(StringComparer.Ordinal)
    {
        ["connection-open"] = ["Npgsql|Client"],
        ["select"] = ["Npgsql|Client"],
        ["failing-select"] = ["Npgsql|Client", "Qyl.Telemetry.Application|Internal"],
        ["connection-close"] = [],
        ["pooled-reopen"] = [],
    };

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed), DemoVariant.NativeAot(DemoRuns.NativeAot)];

    public static string LaneName => "npgsql";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, ConnectionOpen, SelectScalar, ExpectedSqlState);

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
