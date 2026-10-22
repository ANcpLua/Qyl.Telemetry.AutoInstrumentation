namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// Microsoft.Data.SqlClient against the testbed SQL Server (x64 on Railway), in its two trace owners:
/// the source interceptor (QylSqlClientSourceInterceptors=true, with the database client metric) and
/// the specialist DiagnosticListener alone. Each managed and NativeAOT, with and without the
/// statement-text opt-in. The source-interceptor binary is the vendor-warned publish of the AOT
/// classification; the specialist binary is published with warnings relaxed and must match the same
/// SqlClient policy — and still warn, or its vendor boundary must be promoted.
/// </summary>
[TestClass]
[TestCategory("sqlclient")]
public sealed class SqlClientTests(TestContext testContext)
{
    [TestMethod]
    public async Task ExportsExpectedTelemetry()
    {
        var lane = new DemoContext(DemoLanes.ByName("sqlclient"), testContext);
        var port = int.Parse(lane.Environment["QYL_SQLCLIENT_EXPECTED_PORT"], System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (owner, interceptors, metrics) in new[] { ("source_interceptor", true, 4), ("specialist_listener", false, 0) })
        {
            var environment = lane.With(("QYL_SQLCLIENT_EXPECTED_TRACE_OWNER", owner));
            var optIn = lane.With(("QYL_SQLCLIENT_EXPECTED_TRACE_OWNER", owner), ("OTEL_DOTNET_AUTO_SQLCLIENT_SET_DBSTATEMENT_FOR_TEXT", "true"));
            var property = $"-p:QylSqlClientSourceInterceptors={(interceptors ? "true" : "false")}";
            var kind = interceptors ? "source-interceptor" : "specialist";

            lane.BuildManaged("--no-incremental", "--disable-build-servers", property);
            Verify(lane, (await lane.RunManagedAsync($"managed SqlClient {kind} demo", environment)), owner, metrics, port);
            Verify(lane, (await lane.RunManagedAsync($"managed SqlClient {kind} demo (statement opt-in)", optIn)), owner, metrics, port);

            var executable = interceptors ? lane.PublishNativeAot() : PublishSpecialist(lane.Lane, property);
            Verify(lane, (await lane.RunNativeAotAsync(executable, $"NativeAOT SqlClient {kind} demo", environment)), owner, metrics, port);
            Verify(lane, (await lane.RunNativeAotAsync(executable, $"NativeAOT SqlClient {kind} demo (statement opt-in)", optIn)), owner, metrics, port);
        }
    }

    private static string PublishSpecialist(DemoDefinition lane, string property)
    {
        AotPublish.PrebuildGenerator();
        var output = Repo.PublishDirectory(lane.Project, "nativeaot-specialist");
        var outcome = AotPublish.Publish(lane.Project, output, strict: false, [property]);
        Assert.AreEqual(0, outcome.ExitCode, $"specialist-only NativeAOT SqlClient publish failed: exit={outcome.ExitCode}; {outcome.Diagnostics.FirstOrDefault() ?? outcome.Tail}");
        Assert.IsNotEmpty(outcome.Diagnostics, "specialist-only NativeAOT SqlClient publish is now warning-clean; promote its vendor boundary");
        var (valid, detail) = AotPublish.ValidateWarningPolicy(lane.Demo, lane.Project, outcome.Diagnostics);
        Assert.IsTrue(valid, $"specialist-only NativeAOT SqlClient warning policy failed: {detail}");
        var executable = Path.Combine(output, Repo.ExecutableName(lane.Demo));
        Assert.IsTrue(File.Exists(executable), $"NativeAOT SqlClient executable missing: {executable}");
        return executable;
    }

    private static void Verify(DemoContext lane, DemoRun run, string owner, int metrics, int port)
    {
        run.RequireSucceeded().RequireTokens("expected-sql-error=208");
        Assert.AreEqual(run.ExpectedRuntimeMode, run.StringField("RuntimeMode"), $"{run.Label} runtime mode mismatch: expected={run.ExpectedRuntimeMode} actual={run.StringField("RuntimeMode")}");
        Assert.AreEqual(owner, run.StringField("TraceOwner"), $"{run.Label} trace owner mismatch: expected={owner} actual={run.StringField("TraceOwner")}");
        run.RequirePass();
        lane.Telemetry.Spans(run, run.RequireCount("Activities", 4, "SqlClient activities"), "SqlClient");
        var reported = run.RequireCount("Metrics", metrics, "SqlClient duration metrics");
        if (metrics > 0)
            lane.Telemetry.Metrics(run, LaneChecks.Metrics(reported));
        if (owner == "specialist_listener")
            run.RequireTokens($"\"server.port\": \"{port}\"");
    }
}
