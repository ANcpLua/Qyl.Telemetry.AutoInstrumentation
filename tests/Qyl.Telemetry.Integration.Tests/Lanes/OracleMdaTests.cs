using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// ODP.NET's native source against the testbed Oracle, managed and NativeAOT (a vendor-warned
/// publish), in three modes: no add-on, Oracle.ManagedDataAccess.OpenTelemetry with its defaults, and
/// with its enrichment options on. The add-on is enrichment only, and only once its options are on:
/// registering it with nothing configured leaves the spans exactly as ODP.NET emits them; turning its
/// options on adds seven attribute keys and the connection spans, and takes none away. A command is a
/// parent and its one child, not two siblings. OracleConfiguration.OpenTelemetryTracing defaults to
/// true, which is why AddSource alone delivers spans. The failing command is a bare Error without the
/// add-on, and an ORA description with an exception event once RecordException is on;
/// AddDBInfoToDisplayName rewrites the span names to "&lt;verb&gt; HOST:PORT:DATABASE".
/// </summary>
[TestClass]
[TestCategory("oraclemda")]
sealed class OracleMdaTests : SpanDemoLaneTests<OracleMdaTests>, ISpanDemoLane
{
    private const string ManagedNone = "managed-none";
    private const string ManagedAddonDefault = "managed-addon-default";
    private const string ManagedAddonEnriched = "managed-addon-enriched";
    private const string NativeAotNone = "nativeaot-none";
    private const string NativeAotAddonDefault = "nativeaot-addon-default";
    private const string NativeAotAddonEnriched = "nativeaot-addon-enriched";
    private const string ModeNone = "none";
    private const string ModeAddonDefault = "addon-default";
    private const string ModeAddonEnriched = "addon-enriched";
    private const string OracleClient = "Oracle.ManagedDataAccess.Core/Client";
    private const string RootParent = "0000000000000000";
    private const string FailingPhase = "command-failing";
    private const string ExperimentalPrefix = "Experimental.";
    private const string TracingDefault = "oracle-opentelemetry-tracing=True";
    private const string Opened = "oracle-open=1";
    private const string ScalarValue = "scalar-value=1";
    private const string NonQueryRows = "nonquery-rows=-1";
    private const string ExpectedError = "expected-oraclemda-error=Oracle.ManagedDataAccess.Client.OracleException";

    private static readonly string[] CommandPhases = ["command-scalar", "command-nonquery", FailingPhase];
    private static readonly string[] ConnectionPhases = ["open", "close"];

    private static readonly string[] DefaultVocabulary =
        ["db.odp.roundtrip.count", "db.odp.roundtrip.duration", "db.response.returned_rows", "db.system", "qyl.instrumentation.domain"];

    private static readonly string[] EnrichedOnlyKeys =
        ["db.name", "db.odp.connection.id", "db.odp.sql_id", "db.statement", "db.user", "server.address", "server.port"];

    private static readonly Dictionary<string, string> Modes = new(StringComparer.Ordinal)
    {
        [ManagedNone] = ModeNone,
        [ManagedAddonDefault] = ModeAddonDefault,
        [ManagedAddonEnriched] = ModeAddonEnriched,
        [NativeAotNone] = ModeNone,
        [NativeAotAddonDefault] = ModeAddonDefault,
        [NativeAotAddonEnriched] = ModeAddonEnriched,
    };

    private static readonly DemoVariant[] Variants =
    [
        .. new[] { ManagedNone, ManagedAddonDefault, ManagedAddonEnriched }.Select(static key => DemoVariant.Managed(key, ModeVariable(key))),
        .. new[] { NativeAotNone, NativeAotAddonDefault, NativeAotAddonEnriched }.Select(static key => DemoVariant.NativeAot(key, ModeVariable(key))),
    ];

    public static string LaneName => "oraclemda";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, TracingDefault, Opened, ScalarValue, NonQueryRows, ExpectedError);

    public static IEnumerable<object[]> SpanSetSides => Rows(RunKeys, LaneReport.Expected, LaneReport.Actual);

    public static IEnumerable<object[]> CommandPhaseRows => Rows(RunKeys, CommandPhases);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(OutcomeTokens))]
    public void DemoOutput_EachRun_PrintsOutcomeToken(string run, string token) =>
        Assert.Contains(token, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoOutput_EachRun_PrintsItsAddonMode(string run) =>
        Assert.Contains("oracle-mode=" + Modes[run], Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsItsAddonMode(string run) =>
        Assert.AreEqual(Modes[run], Run(run).StringField("AddonMode"));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ObservedActivities_EachRun_IncludeBackgroundHttpExport(string run) =>
        Assert.Contains(static activity => ReportedActivity.Text(activity, "Phase") == "background"
                                           && ReportedActivity.Text(activity, "Source") == "System.Net.Http", Run(run).Array("ObservedActivities"));

    [TestMethod]
    [DynamicData(nameof(SpanSetSides))]
    public void SpanSets_EachRunAndSide_MatchThePinnedSpansPerPhase(string run, string side) =>
        Assert.AreSequenceEqual(LaneReport.SpanSets(PinnedSpanSets(Modes[run]), sort: true), LaneReport.SpanSets(Run(run), "SpanSets", "Phase", side),
            SequenceOrder.InAnyOrder);

    [TestMethod]
    [DynamicData(nameof(CommandPhaseRows))]
    public void OracleCommand_EachPhase_HasARootSpan(string run, string phase) =>
        Assert.IsNotNull(RootSpanId(Run(run), phase));

    [TestMethod]
    [DynamicData(nameof(CommandPhaseRows))]
    public void OracleCommand_EachPhase_HasExactlyOneChildOfItsRoot(string run, string phase) =>
        Assert.HasCount(1, Run(run).Array("Activities").Where(activity =>
            ReportedActivity.Text(activity, "Phase") == phase && ReportedActivity.Text(activity, "ParentSpanId") == RootSpanId(Run(run), phase)));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ExcludedSources_EachRun_AreOnlyExperimental(string run) =>
        Assert.IsEmpty(LaneReport.PrefixViolations(Run(run).Strings("ExcludedSources"), ExperimentalPrefix));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ExcludedSources_EachRun_AreNotEmpty(string run) =>
        Assert.IsNotEmpty(Run(run).Array("ExcludedSources"));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void TagVocabulary_EachMode_IsExactlyTheModesKeys(string run) =>
        Assert.AreSequenceEqual(Vocabulary(Modes[run]), Run(run).Strings("TagVocabulary"));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsOneActivityPerPinnedSpan(string run) =>
        Assert.HasCount(PinnedSpanSets(Modes[run]).Values.Sum(static spans => spans.Length), Run(run).Array("Activities"));

    [TestMethod]
    public void AddonDefault_NoOptions_LeavesTheSpansUnchanged() =>
        Assert.AreSequenceEqual(Shape(Run(ManagedNone)), Shape(Run(ManagedAddonDefault)));

    [TestMethod]
    public void AddonEnriched_OptionsOn_AddsExactlyTheEnrichmentKeys() =>
        Assert.AreSequenceEqual(EnrichedOnlyKeys.Order(StringComparer.Ordinal),
            Run(ManagedAddonEnriched).Strings("TagVocabulary").Except(Run(ManagedNone).Strings("TagVocabulary")).Order(StringComparer.Ordinal));

    [TestMethod]
    public void AddonEnriched_OptionsOn_RemovesNoOdpNetKey() =>
        Assert.IsEmpty(Run(ManagedNone).Strings("TagVocabulary").Except(Run(ManagedAddonEnriched).Strings("TagVocabulary")));

    [TestMethod]
    public void FailingRoot_WithoutAddon_HasErrorStatus() =>
        Assert.AreEqual("Error", ReportedActivity.Text(FailingRoot(Run(ManagedNone)), "Status"));

    [TestMethod]
    public void FailingRoot_WithoutAddon_HasNullStatusDescription() =>
        Assert.AreEqual(JsonValueKind.Null, FailingRoot(Run(ManagedNone)).GetProperty("StatusDescription").ValueKind);

    [TestMethod]
    public void FailingRoot_WithoutAddon_HasNoEvents() =>
        Assert.AreEqual(0, FailingRoot(Run(ManagedNone)).GetProperty("Events").GetArrayLength());

    [TestMethod]
    public void FailingRoot_AddonEnriched_HasErrorStatus() =>
        Assert.AreEqual("Error", ReportedActivity.Text(FailingRoot(Run(ManagedAddonEnriched)), "Status"));

    [TestMethod]
    public void FailingRoot_AddonEnriched_HasAStatusDescription() =>
        Assert.IsFalse(string.IsNullOrEmpty(ReportedActivity.Text(FailingRoot(Run(ManagedAddonEnriched)), "StatusDescription")));

    [TestMethod]
    public void FailingRoot_AddonEnriched_HasOneExceptionEvent() =>
        Assert.AreSequenceEqual(["exception"], FailingRoot(Run(ManagedAddonEnriched)).TryGetProperty("Events", out var events)
            ? events.EnumerateArray().Select(static item => ReportedActivity.Text(item, "Name"))
            : []);

    [TestMethod]
    public void DisplayNames_AddonEnriched_CarryTheDatabaseInfo() =>
        Assert.DoesNotContain(static activity => !(ReportedActivity.Text(activity, "Name") ?? string.Empty).EndsWith(
                $" {ReportedActivity.Tag(activity, "server.address")}:{ReportedActivity.Tag(activity, "server.port")}:{ReportedActivity.Tag(activity, "db.name")}",
                StringComparison.Ordinal),
            Run(ManagedAddonEnriched).Array("Activities"));

    private static (string, string) ModeVariable(string key) => ("QYL_ORACLE_ADDON_MODE", Modes[key]);

    private static Dictionary<string, string[]> PinnedSpanSets(string mode)
    {
        var connection = mode == ModeAddonEnriched ? new[] { OracleClient, OracleClient } : [];
        var sets = CommandPhases.ToDictionary(static phase => phase, static _ => new[] { OracleClient, OracleClient }, StringComparer.Ordinal);
        foreach (var phase in ConnectionPhases)
            sets[phase] = connection;
        return sets;
    }

    private static string[] Vocabulary(string mode) => mode == ModeAddonEnriched
        ? [.. DefaultVocabulary.Concat(EnrichedOnlyKeys).Order(StringComparer.Ordinal)]
        : [.. DefaultVocabulary.Order(StringComparer.Ordinal)];

    private static string? RootSpanId(DemoRun run, string phase) =>
        run.Array("Activities")
            .Where(activity => ReportedActivity.Text(activity, "ParentSpanId") == RootParent && ReportedActivity.Text(activity, "Phase") == phase)
            .Select(static activity => ReportedActivity.Text(activity, "SpanId"))
            .LastOrDefault();

    private static JsonElement FailingRoot(DemoRun run) =>
        run.Array("Activities").First(static activity =>
            ReportedActivity.Text(activity, "Phase") == FailingPhase && ReportedActivity.Text(activity, "ParentSpanId") == RootParent);

    private static string[] Shape(DemoRun run) => [.. run.Array("Activities")
        .Select(static activity => string.Join("|",
            ReportedActivity.Text(activity, "Phase"),
            ReportedActivity.Text(activity, "Name"),
            ReportedActivity.Text(activity, "Status"),
            string.Join(",", ReportedActivity.TagKeys(activity).Order(StringComparer.Ordinal))))
        .Order(StringComparer.Ordinal)];
}
