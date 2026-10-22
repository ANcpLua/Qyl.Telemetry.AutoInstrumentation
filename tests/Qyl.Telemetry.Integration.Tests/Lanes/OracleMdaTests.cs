using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// ODP.NET's native source against the testbed Oracle, managed and NativeAOT (a vendor-warned
/// publish), in three modes: no add-on, Oracle.ManagedDataAccess.OpenTelemetry with its defaults, and
/// with its enrichment options on. The add-on is enrichment only, and only once its options are on.
/// </summary>
[TestClass]
[TestCategory("oraclemda")]
public sealed class OracleMdaTests(TestContext testContext)
{
    private const string OracleClient = "Oracle.ManagedDataAccess.Core/Client";
    private const string ModeNone = "none";
    private const string ModeAddonDefault = "addon-default";
    private const string ModeAddonEnriched = "addon-enriched";
    private static readonly string[] Modes = [ModeNone, ModeAddonDefault, ModeAddonEnriched];
    private static readonly string[] CommandPhases = ["command-scalar", "command-nonquery", "command-failing"];
    private static readonly string[] ConnectionPhases = ["open", "close"];
    private const string RootParent = "0000000000000000";

    // The four keys ODP.NET writes on its own, plus the one the qyl processor stamps.
    private static readonly string[] DefaultVocabulary =
        ["db.odp.roundtrip.count", "db.odp.roundtrip.duration", "db.response.returned_rows", "db.system", "qyl.instrumentation.domain"];

    // What the add-on adds once its enrichment options are on. None of these gate whether a span exists.
    private static readonly string[] EnrichedOnlyKeys =
        ["db.name", "db.odp.connection.id", "db.odp.sql_id", "db.statement", "db.user", "server.address", "server.port"];

    [TestMethod]
    public async Task ExportsExpectedTelemetry()
    {
        var lane = new DemoContext(DemoLanes.ByName("oraclemda"), testContext);
        lane.BuildManaged();
        var executable = lane.PublishNativeAot();
        var managed = new Dictionary<string, DemoRun>(StringComparer.Ordinal);
        foreach (var mode in Modes)
            managed[mode] = Verify(lane, (await lane.RunManagedAsync($"managed Oracle MDA demo ({mode})", lane.With(("QYL_ORACLE_ADDON_MODE", mode)))), mode);
        foreach (var mode in Modes)
            Verify(lane, (await lane.RunNativeAotAsync(executable, $"NativeAOT Oracle MDA demo ({mode})", lane.With(("QYL_ORACLE_ADDON_MODE", mode)))), mode);
        VerifyAddonDelta(managed);
    }

    private static Dictionary<string, string[]> ExpectedSpanSets(string mode)
    {
        var connection = mode == ModeAddonEnriched ? new[] { OracleClient, OracleClient } : [];
        var sets = CommandPhases.ToDictionary(static phase => phase, static _ => new[] { OracleClient, OracleClient }, StringComparer.Ordinal);
        foreach (var phase in ConnectionPhases)
            sets[phase] = connection;
        return sets;
    }

    private static DemoRun Verify(DemoContext lane, DemoRun run, string mode)
    {
        run.RequireSucceeded().RequireTokens(
            $"oracle-mode={mode}",
            // OracleConfiguration.OpenTelemetryTracing defaults to true, which is why AddSource alone delivers spans.
            "oracle-opentelemetry-tracing=True",
            "oracle-open=1",
            "scalar-value=1",
            "nonquery-rows=-1",
            "expected-oraclemda-error=Oracle.ManagedDataAccess.Client.OracleException");
        Assert.AreEqual(run.ExpectedRuntimeMode, run.StringField("RuntimeMode"), $"{run.Label} runtime mode mismatch: {run.StringField("RuntimeMode")}");
        Assert.AreEqual(mode, run.StringField("AddonMode"), $"{run.Label} add-on mode mismatch: expected={mode} actual={run.StringField("AddonMode")}");
        run.RequirePass();
        var observed = run.Array("ObservedActivities").EnumerateArray().ToArray();
        Assert.Contains(static activity => ReportedActivity.Text(activity, "Phase") == "background"
            && ReportedActivity.Text(activity, "Source") == "System.Net.Http", observed,
            $"{run.Label}: the real in-operation batch export did not exercise background HTTP activity attribution");

        var sets = ExpectedSpanSets(mode);
        LaneChecks.SpanSets(run, "SpanSets", "Phase", sets, sortExpected: true);

        // A command is a parent and its one child, not two siblings.
        var activities = run.Array("Activities").EnumerateArray().ToArray();
        var roots = activities.Where(static activity => ReportedActivity.Text(activity, "ParentSpanId") == RootParent)
            .GroupBy(static activity => ReportedActivity.Text(activity, "Phase") ?? string.Empty)
            .ToDictionary(static group => group.Key, static group => ReportedActivity.Text(group.Last(), "SpanId"), StringComparer.Ordinal);
        foreach (var phase in CommandPhases)
        {
            Assert.IsTrue(roots.ContainsKey(phase), $"{run.Label} {phase} produced no root span");
            var children = activities.Count(activity => ReportedActivity.Text(activity, "Phase") == phase && ReportedActivity.Text(activity, "ParentSpanId") == roots[phase]);
            Assert.AreEqual(1, children, $"{run.Label} {phase} expected exactly 1 child of its root span, got {children}");
        }

        LaneChecks.ExcludedWithPrefix(run, "ExcludedSources", "Experimental.", requireSome: true);
        string[] vocabulary = mode == ModeAddonEnriched ? [.. DefaultVocabulary.Concat(EnrichedOnlyKeys).Order(StringComparer.Ordinal)] : [.. DefaultVocabulary.Order(StringComparer.Ordinal)];
        LaneChecks.ExactStrings(run, "TagVocabulary", vocabulary);
        lane.Telemetry.Spans(run, run.RequireCount("Activities", sets.Values.Sum(static spans => spans.Length), "ODP.NET activities"), "ODP.NET");
        return run;
    }

    /// <summary>Registering the add-on with nothing configured leaves the spans exactly as ODP.NET emits
    /// them; turning its options on adds seven attribute keys and the connection spans, and takes none away.</summary>
    private static void VerifyAddonDelta(IReadOnlyDictionary<string, DemoRun> reports)
    {
        static string[] Shape(DemoRun run) => [.. run.Array("Activities").EnumerateArray()
            .Select(static activity => string.Join("|",
                ReportedActivity.Text(activity, "Phase"),
                ReportedActivity.Text(activity, "Name"),
                ReportedActivity.Text(activity, "Status"),
                string.Join(",", ReportedActivity.TagKeys(activity).Order(StringComparer.Ordinal))))
            .Order(StringComparer.Ordinal)];

        Assert.IsTrue(Shape(reports[ModeNone]).SequenceEqual(Shape(reports[ModeAddonDefault])), $"AddOracleDataProviderInstrumentation() with no options changed the spans:\nnone={string.Join("\n", Shape(reports[ModeNone]))}\naddon-default={string.Join("\n", Shape(reports[ModeAddonDefault]))}");

        var defaultKeys = DemoRun.Strings(reports[ModeNone].Array("TagVocabulary")).ToHashSet(StringComparer.Ordinal);
        var enrichedKeys = DemoRun.Strings(reports[ModeAddonEnriched].Array("TagVocabulary")).ToHashSet(StringComparer.Ordinal);
        var added = enrichedKeys.Except(defaultKeys).Order(StringComparer.Ordinal).ToArray();
        Assert.IsTrue(added.SequenceEqual(EnrichedOnlyKeys.Order(StringComparer.Ordinal)), $"add-on attribute delta: expected [{string.Join(", ", EnrichedOnlyKeys)}], got [{string.Join(", ", added)}]");
        var removed = defaultKeys.Except(enrichedKeys).ToArray();
        Assert.IsEmpty(removed, $"the add-on removed attributes ODP.NET writes on its own: [{string.Join(", ", removed)}]");

        // The failing command: a bare status code without the add-on, an ORA description and an
        // exception event with RecordException on.
        static JsonElement FailingRoot(DemoRun run) => run.Array("Activities").EnumerateArray()
            .FirstOrDefault(static activity => ReportedActivity.Text(activity, "Phase") == "command-failing" && ReportedActivity.Text(activity, "ParentSpanId") == RootParent) is { ValueKind: JsonValueKind.Object } root
            ? root
            : throw new AssertFailedException($"no failing root span in the {run.StringField("AddonMode")} run");

        var bare = FailingRoot(reports[ModeNone]);
        var enriched = FailingRoot(reports[ModeAddonEnriched]);
        Assert.IsTrue(ReportedActivity.Text(bare, "Status") == "Error"
                     && bare.TryGetProperty("StatusDescription", out var description) && description.ValueKind is JsonValueKind.Null
                     && bare.TryGetProperty("Events", out var bareEvents) && bareEvents.GetArrayLength() == 0,
            $"the default failing root is no longer a bare Error: {bare}");
        Assert.IsTrue(ReportedActivity.Text(enriched, "Status") == "Error" && !string.IsNullOrEmpty(ReportedActivity.Text(enriched, "StatusDescription")),
            $"the enriched failing root has no status description: {enriched}");
        var events = enriched.TryGetProperty("Events", out var enrichedEvents) ? enrichedEvents.EnumerateArray().Select(static item => ReportedActivity.Text(item, "Name")).ToArray() : [];
        Assert.IsTrue(events.SequenceEqual(["exception"]), $"the enriched failing root has no single exception event: {string.Join(", ", events)}");

        // AddDBInfoToDisplayName rewrites the span names to "<verb> HOST:PORT:DATABASE".
        foreach (var activity in reports[ModeAddonEnriched].Array("Activities").EnumerateArray())
        {
            var suffix = $"{ReportedActivity.Tag(activity, "server.address")}:{ReportedActivity.Tag(activity, "server.port")}:{ReportedActivity.Tag(activity, "db.name")}";
            var name = ReportedActivity.Text(activity, "Name") ?? string.Empty;
            Assert.IsTrue(name.EndsWith(" " + suffix, StringComparison.Ordinal), $"enriched display name does not carry the database info: {name}");
        }
    }
}
