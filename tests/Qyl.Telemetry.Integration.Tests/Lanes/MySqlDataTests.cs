using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// MySql.Data's connector-net source against the testbed MySQL, managed, with and without the
/// stability opt-in it does not read; and its NativeAOT binary pinned to the exact abort inside the
/// driver, so the lane fails — and gets promoted — the day the driver runs under NativeAOT.
/// </summary>
[TestClass]
[TestCategory("mysqldata")]
public sealed class MySqlDataTests(TestContext testContext)
{
    private const string ConnectorNetClient = "connector-net/Client";

    // A physical open traces the driver's five handshake statements; the Connection activity it
    // starts stops only at Close/Dispose, so it lands in the close phase. A pooled re-open traces
    // nothing at open. Every user command is exactly one span.
    private static readonly Dictionary<string, string[]> SpanSets = new(StringComparer.Ordinal)
    {
        ["physical-open"] = [ConnectorNetClient, ConnectorNetClient, ConnectorNetClient, ConnectorNetClient, ConnectorNetClient],
        ["command-select"] = [ConnectorNetClient],
        ["command-failing-select"] = [ConnectorNetClient],
        ["close"] = [ConnectorNetClient],
        ["pooled-open"] = [],
        ["pooled-close"] = [ConnectorNetClient],
    };

    // The first Open() runs MySqlConfiguration's static constructor, which reads ConfigurationManager,
    // and the config host it activates by name is gone from the trimmed application.
    private static readonly string[] NativeAotAbortMarkers =
    [
        "MySql.Data.MySqlClient.MySqlConfiguration..cctor()",
        "No parameterless constructor defined for type 'System.Configuration.ClientConfigurationHost'.",
    ];

    [TestMethod]
    public async Task ExportsExpectedTelemetry()
    {
        var lane = new DemoContext(DemoLanes.ByName("mysqldata"), testContext);
        lane.BuildManaged();
        var executable = lane.PublishNativeAot();
        var managed = Verify(lane, (await lane.RunManagedAsync("managed MySql.Data demo")));

        // The driver reads no stability opt-in: the same run with the variable set has to produce the same spans.
        var optIn = Verify(lane, (await lane.RunManagedAsync("managed MySql.Data demo (stability opt-in)", lane.With(("OTEL_SEMCONV_STABILITY_OPT_IN", "database")))));
        VerifyStatementCaptureIsUnconditional(managed, optIn);
        VerifyNativeAotLimitation((await lane.RunNativeAotAsync(executable, "NativeAOT MySql.Data demo")));
    }

    private static DemoRun Verify(DemoContext lane, DemoRun run)
    {
        run.RequireSucceeded()
            .RequireTokens("physical-open=1", "select-scalar=1", "expected-mysqldata-error=MySql.Data.MySqlClient.MySqlException", "pooled-open=1")
            .RequireReportPassed();
        LaneChecks.SpanSets(run, "SpanSets", "Phase", SpanSets, sortExpected: true);
        LaneChecks.ExcludedWithPrefix(run, "ExcludedSources", "Experimental.", requireSome: true);

        // MySql.Data keeps writing to a stopped activity: the failing command reads ERROR at the instant
        // a processor sees it and OK once the driver is done. Both readings are asserted.
        var total = SpanSets.Values.Sum(static spans => spans.Length);
        var rereadings = run.RequireCount("StatusCodeRereadings", total, "status-code re-readings").EnumerateArray().ToArray();
        var failing = rereadings.Where(static entry => ReportedActivity.Text(entry, "Phase") == "command-failing-select").ToArray();
        Assert.HasCount(1, failing, $"{run.Label} expected 1 re-reading for the failing command, got {failing.Length}");
        Assert.IsTrue(ReportedActivity.Text(failing[0], "AtStop") == "ERROR" && ReportedActivity.Text(failing[0], "AfterStop") == "OK",
            $"{run.Label} failing command status codes changed shape: {failing[0]}");
        foreach (var entry in rereadings.Where(static entry => ReportedActivity.Text(entry, "Phase") != "command-failing-select"))
        {
            Assert.IsTrue(ReportedActivity.Text(entry, "AtStop") == "OK" && ReportedActivity.Text(entry, "AfterStop") == "OK",
                $"{run.Label} expected OK/OK on a succeeding span, got {entry}");
        }

        // The report holds each span's tags as the processor saw them at stop; the batch exporter reads
        // them after the driver is done, when every otel.status_code reads OK (asserted just above).
        var exported = new System.Text.Json.Nodes.JsonArray();
        foreach (var activity in run.RequireCount("Activities", total, "connector-net activities").EnumerateArray())
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(activity.GetRawText())!.AsObject();
            if (node["Tags"] is System.Text.Json.Nodes.JsonObject tags && tags.ContainsKey("otel.status_code"))
                tags["otel.status_code"] = "OK";
            exported.Add(node);
        }

        lane.Telemetry.Spans(run, JsonSerializer.SerializeToElement(exported), "connector-net");
        return run;
    }

    /// <summary>db.statement carries the full command text with no opt-in: AddConnectorNet() is
    /// AddSource("connector-net") and nothing else, and the stability opt-in reaches no driver code.
    /// The proof is that the two reports are identical.</summary>
    private static void VerifyStatementCaptureIsUnconditional(DemoRun defaults, DemoRun optIn)
    {
        static string[] Shape(DemoRun run) => [.. run.Array("Activities").EnumerateArray()
            .Select(static activity => string.Join("|",
                ReportedActivity.Text(activity, "Phase"),
                ReportedActivity.Text(activity, "Name"),
                ReportedActivity.Text(activity, "Kind"),
                string.Join(",", ReportedActivity.TagKeys(activity).Order(StringComparer.Ordinal)),
                ReportedActivity.Tag(activity, "db.statement") ?? string.Empty))];

        var (left, right) = (Shape(defaults), Shape(optIn));
        Assert.IsTrue(left.SequenceEqual(right), $"OTEL_SEMCONV_STABILITY_OPT_IN changed the MySql.Data span shape:\ndefault={string.Join("\n", left)}\nopt-in={string.Join("\n", right)}");

        var statements = defaults.Array("Activities").EnumerateArray()
            .Where(static activity => ReportedActivity.HasTag(activity, "db.statement"))
            .GroupBy(static activity => ReportedActivity.Text(activity, "Phase") ?? string.Empty)
            .ToDictionary(static group => group.Key, static group => ReportedActivity.Tag(group.Last(), "db.statement"), StringComparer.Ordinal);
        Assert.AreEqual("SELECT 1 AS qyl_probe_value", statements.GetValueOrDefault("command-select"), $"db.statement is not the full command text: {string.Join(", ", statements)}");
        Assert.AreEqual("SELECT qyl_missing_column FROM qyl_missing_table", statements.GetValueOrDefault("command-failing-select"),
            $"db.statement is not the full command text on the failing command: {string.Join(", ", statements)}");
    }

    private static void VerifyNativeAotLimitation(DemoRun run)
    {
        Assert.AreNotEqual(0, run.Process.ExitCode,
            "the NativeAOT MySql.Data demo now runs: replace this pinned limitation with the full Verify() the managed runs use");
        var output = run.Process.Stdout + run.Process.Stderr;
        foreach (var marker in NativeAotAbortMarkers)
            Assert.IsTrue(output.Contains(marker, StringComparison.Ordinal), $"the NativeAOT MySql.Data demo failed somewhere new — '{marker}' is missing\n{run.Process.Describe()}");
        Log.Information("  NativeAOT MySql.Data run pinned to MySqlConfiguration..cctor / ConfigurationManager");
    }
}
