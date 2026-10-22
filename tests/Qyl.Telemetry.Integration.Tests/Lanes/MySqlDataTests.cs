using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// MySql.Data's connector-net source against the testbed MySQL, managed, with and without the
/// stability opt-in it does not read; and its NativeAOT binary pinned to the exact abort inside the
/// driver, so the lane fails — and gets promoted — the day the driver runs under NativeAOT.
/// A physical open traces the driver's five handshake statements; the Connection activity it starts
/// stops only at Close/Dispose, so it lands in the close phase. A pooled re-open traces nothing at
/// open. Every user command is exactly one span. MySql.Data keeps writing to a stopped activity: the
/// failing command reads ERROR at the instant a processor sees it and OK once the driver is done, and
/// the batch exporter reads every otel.status_code after the driver is done. db.statement carries the
/// full command text with no opt-in: AddConnectorNet() is AddSource("connector-net") and nothing else.
/// Under NativeAOT the first Open() runs MySqlConfiguration's static constructor, which reads
/// ConfigurationManager, and the config host it activates by name is gone from the trimmed application.
/// </summary>
[TestClass]
[TestCategory("mysqldata")]
sealed class MySqlDataTests : SpanDemoLaneTests<MySqlDataTests>, ISpanDemoLane
{
    private const string ManagedStabilityOptIn = "managed-stability-opt-in";
    private const string ConnectorNetClient = "connector-net/Client";
    private const string FailingPhase = "command-failing-select";
    private const string SelectPhase = "command-select";
    private const string ExperimentalPrefix = "Experimental.";
    private const string PhysicalOpen = "physical-open=1";
    private const string SelectScalar = "select-scalar=1";
    private const string ExpectedError = "expected-mysqldata-error=MySql.Data.MySqlClient.MySqlException";
    private const string PooledOpen = "pooled-open=1";
    private const string ConfigurationConstructor = "MySql.Data.MySqlClient.MySqlConfiguration..cctor()";
    private const string MissingConfigurationHost = "No parameterless constructor defined for type 'System.Configuration.ClientConfigurationHost'.";

    private static readonly Dictionary<string, string[]> SpanSets = new(StringComparer.Ordinal)
    {
        ["physical-open"] = [ConnectorNetClient, ConnectorNetClient, ConnectorNetClient, ConnectorNetClient, ConnectorNetClient],
        [SelectPhase] = [ConnectorNetClient],
        [FailingPhase] = [ConnectorNetClient],
        ["close"] = [ConnectorNetClient],
        ["pooled-open"] = [],
        ["pooled-close"] = [ConnectorNetClient],
    };

    private static readonly DemoVariant[] Variants =
    [
        DemoVariant.Managed(DemoRuns.Managed),
        DemoVariant.Managed(ManagedStabilityOptIn, ("OTEL_SEMCONV_STABILITY_OPT_IN", "database")),
        DemoVariant.NativeAot(DemoRuns.NativeAot),
    ];

    private static readonly string[] ManagedRuns = [DemoRuns.Managed, ManagedStabilityOptIn];

    private static int TotalSpans => SpanSets.Values.Sum(static spans => spans.Length);

    public static string LaneName => "mysqldata";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IReadOnlyList<string> PinnedFailures => [DemoRuns.NativeAot];

    public static IEnumerable<object[]> OutcomeTokens => Rows(ManagedRuns, PhysicalOpen, SelectScalar, ExpectedError, PooledOpen);

    public static IEnumerable<object[]> SpanSetSides => Rows(ManagedRuns, LaneReport.Expected, LaneReport.Actual);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run)
    {
        foreach (var activity in run.Array("Activities"))
        {
            var node = JsonNode.Parse(activity.GetRawText())!.AsObject();
            if (node["Tags"] is JsonObject tags && tags.ContainsKey("otel.status_code"))
                tags["otel.status_code"] = "OK";
            yield return JsonSerializer.SerializeToElement(node);
        }
    }

    [TestMethod]
    [DynamicData(nameof(OutcomeTokens))]
    public void DemoOutput_ManagedRun_PrintsOutcomeToken(string run, string token) =>
        Assert.Contains(token, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(SpanSetSides))]
    public void SpanSets_EachRunAndSide_MatchThePinnedSpansPerPhase(string run, string side) =>
        Assert.AreSequenceEqual(LaneReport.SpanSets(SpanSets, sort: true), LaneReport.SpanSets(Run(run), "SpanSets", "Phase", side), SequenceOrder.InAnyOrder);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ExcludedSources_ManagedRun_AreOnlyExperimental(string run) =>
        Assert.IsEmpty(LaneReport.PrefixViolations(Run(run).Strings("ExcludedSources"), ExperimentalPrefix));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ExcludedSources_ManagedRun_AreNotEmpty(string run) =>
        Assert.IsNotEmpty(Run(run).Array("ExcludedSources"));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void StatusCodeRereadings_ManagedRun_CoverEverySpan(string run) =>
        Assert.HasCount(TotalSpans, Run(run).Array("StatusCodeRereadings"));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void StatusCodeRereadings_FailingCommand_ReadErrorAtStopAndOkAfter(string run)
    {
        var failing = Assert.ContainsSingle(Run(run).Array("StatusCodeRereadings").Where(static entry => ReportedActivity.Text(entry, "Phase") == FailingPhase));

        Assert.AreEqual(("ERROR", "OK"), (ReportedActivity.Text(failing, "AtStop"), ReportedActivity.Text(failing, "AfterStop")));
    }

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void StatusCodeRereadings_SucceedingSpans_ReadOkAtStopAndAfter(string run) =>
        Assert.DoesNotContain(static entry => ReportedActivity.Text(entry, "AtStop") != "OK" || ReportedActivity.Text(entry, "AfterStop") != "OK",
            Run(run).Array("StatusCodeRereadings").Where(static entry => ReportedActivity.Text(entry, "Phase") != FailingPhase));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_ManagedRun_ReportsOneActivityPerPinnedSpan(string run) =>
        Assert.HasCount(TotalSpans, Run(run).Array("Activities"));

    [TestMethod]
    public void SpanShape_StabilityOptIn_StaysTheSame() =>
        Assert.AreSequenceEqual(Shape(Run(DemoRuns.Managed)), Shape(Run(ManagedStabilityOptIn)));

    [TestMethod]
    [DataRow(SelectPhase, "SELECT 1 AS qyl_probe_value")]
    [DataRow(FailingPhase, "SELECT qyl_missing_column FROM qyl_missing_table")]
    public void DbStatement_WithoutOptIn_IsTheFullCommandText(string phase, string statement) =>
        Assert.AreEqual(statement, Run(DemoRuns.Managed).Array("Activities")
            .Where(activity => ReportedActivity.Text(activity, "Phase") == phase && ReportedActivity.HasTag(activity, "db.statement"))
            .Select(static activity => ReportedActivity.Tag(activity, "db.statement"))
            .LastOrDefault());

    [TestMethod]
    public void DemoProcess_NativeAot_StillAborts() =>
        Assert.AreNotEqual(0, Run(DemoRuns.NativeAot).Process.ExitCode, Run(DemoRuns.NativeAot).Process.Describe());

    [TestMethod]
    [DataRow(ConfigurationConstructor)]
    [DataRow(MissingConfigurationHost)]
    public void DemoProcess_NativeAot_AbortsInsideMySqlConfiguration(string marker) =>
        Assert.Contains(marker, Run(DemoRuns.NativeAot).Process.Stdout + Run(DemoRuns.NativeAot).Process.Stderr, StringComparison.Ordinal);

    private static string[] Shape(DemoRun run) => [.. run.Array("Activities")
        .Select(static activity => string.Join("|",
            ReportedActivity.Text(activity, "Phase"),
            ReportedActivity.Text(activity, "Name"),
            ReportedActivity.Text(activity, "Kind"),
            string.Join(",", ReportedActivity.TagKeys(activity).Order(StringComparer.Ordinal)),
            ReportedActivity.Tag(activity, "db.statement") ?? string.Empty))];
}
