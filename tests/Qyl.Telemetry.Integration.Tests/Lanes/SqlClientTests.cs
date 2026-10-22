using System.Globalization;
using System.Text.Json;

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
sealed class SqlClientTests : SpanDemoLaneTests<SqlClientTests>, ISpanDemoLane
{
    private const string InterceptorManaged = "interceptor-managed";
    private const string InterceptorManagedOptIn = "interceptor-managed-statement-opt-in";
    private const string InterceptorNativeAot = "interceptor-nativeaot";
    private const string InterceptorNativeAotOptIn = "interceptor-nativeaot-statement-opt-in";
    private const string SpecialistManaged = "specialist-managed";
    private const string SpecialistManagedOptIn = "specialist-managed-statement-opt-in";
    private const string SpecialistNativeAot = "specialist-nativeaot";
    private const string SpecialistNativeAotOptIn = "specialist-nativeaot-statement-opt-in";
    private const string SourceInterceptor = "source_interceptor";
    private const string SpecialistListener = "specialist_listener";
    private const string ExpectedSqlError = "expected-sql-error=208";
    private const int ReportedActivities = 4;
    private const int InterceptorMetricSeries = 4;

    private static readonly string[] InterceptorRuns = [InterceptorManaged, InterceptorManagedOptIn, InterceptorNativeAot, InterceptorNativeAotOptIn];
    private static readonly string[] SpecialistRuns = [SpecialistManaged, SpecialistManagedOptIn, SpecialistNativeAot, SpecialistNativeAotOptIn];

    private static int s_testbedPort;
    private static AotPublishOutcome? s_specialistPublish;
    private static (bool Valid, string Detail) s_specialistPolicy;

    public static string LaneName => "sqlclient";

    public static IReadOnlyList<string> RunKeys => [.. InterceptorRuns, .. SpecialistRuns];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, ExpectedSqlError);

    public static IEnumerable<object[]> InterceptorRunRows => InterceptorRuns.Select(static run => new object[] { run });

    public static IEnumerable<object[]> SpecialistRunRows => SpecialistRuns.Select(static run => new object[] { run });

    public static IEnumerable<object[]> TraceOwners => Rows(InterceptorRuns, SourceInterceptor).Concat(Rows(SpecialistRuns, SpecialistListener));

    private static AotPublishOutcome SpecialistPublish =>
        s_specialistPublish ?? throw new InvalidOperationException("the specialist NativeAOT publish has not run");

    public static async Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane)
    {
        s_testbedPort = int.Parse(lane.Environment["QYL_SQLCLIENT_EXPECTED_PORT"], CultureInfo.InvariantCulture);
        var runs = new Dictionary<string, DemoRun>(StringComparer.Ordinal);
        foreach (var (owner, interceptors, keys) in new[] { (SourceInterceptor, true, InterceptorRuns), (SpecialistListener, false, SpecialistRuns) })
        {
            var environment = lane.With(("QYL_SQLCLIENT_EXPECTED_TRACE_OWNER", owner));
            var optIn = lane.With(("QYL_SQLCLIENT_EXPECTED_TRACE_OWNER", owner), ("OTEL_DOTNET_AUTO_SQLCLIENT_SET_DBSTATEMENT_FOR_TEXT", "true"));
            var property = $"-p:QylSqlClientSourceInterceptors={(interceptors ? "true" : "false")}";

            lane.BuildManaged("--no-incremental", "--disable-build-servers", property);
            runs[keys[0]] = await lane.RunManagedAsync(keys[0], environment, []);
            runs[keys[1]] = await lane.RunManagedAsync(keys[1], optIn, []);

            var executable = interceptors ? lane.PublishNativeAot() : PublishSpecialist(lane.Lane, property);
            runs[keys[2]] = await lane.RunNativeAotAsync(executable, keys[2], environment, []);
            runs[keys[3]] = await lane.RunNativeAotAsync(executable, keys[3], optIn, []);
        }

        return runs;
    }

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(OutcomeTokens))]
    public void DemoOutput_EachRun_PrintsOutcomeToken(string run, string token) =>
        Assert.Contains(token, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(TraceOwners))]
    public void DemoReport_EachRun_NamesItsTraceOwner(string run, string owner) =>
        Assert.AreEqual(owner, Run(run).StringField("TraceOwner"));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsFourActivities(string run) =>
        Assert.HasCount(ReportedActivities, Run(run).Array("Activities"));

    [TestMethod]
    [DynamicData(nameof(InterceptorRunRows))]
    public void DemoReport_SourceInterceptor_ReportsFourDurationSeries(string run) =>
        Assert.HasCount(InterceptorMetricSeries, Run(run).Array("Metrics"));

    [TestMethod]
    [DynamicData(nameof(SpecialistRunRows))]
    public void DemoReport_SpecialistListener_ReportsNoDurationSeries(string run) =>
        Assert.IsEmpty(Run(run).Array("Metrics"));

    [TestMethod]
    [DynamicData(nameof(InterceptorRunRows))]
    public void ReportedMetrics_SourceInterceptor_ReachTheCapture(string run)
    {
        var unmatched = CapturedTelemetry.UnmatchedMetrics(Run(run), Lane.ServiceName, LaneReport.Metrics(Run(run).Array("Metrics")));

        Assert.IsEmpty(unmatched, string.Join("\n", unmatched));
    }

    [TestMethod]
    [DynamicData(nameof(SpecialistRunRows))]
    public void DemoOutput_SpecialistListener_PrintsTheTestbedServerPort(string run) =>
        Assert.Contains($"\"server.port\": \"{s_testbedPort}\"", Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    public void SpecialistPublish_NativeAot_ExitsWithZero() =>
        Assert.AreEqual(0, SpecialistPublish.ExitCode, SpecialistPublish.Diagnostics.FirstOrDefault() ?? SpecialistPublish.Tail);

    [TestMethod]
    public void SpecialistPublish_NativeAot_StillReportsVendorWarnings() =>
        Assert.IsNotEmpty(SpecialistPublish.Diagnostics, "specialist-only NativeAOT SqlClient publish is now warning-clean; promote its vendor boundary");

    [TestMethod]
    public void SpecialistPublish_NativeAot_SatisfiesTheSqlClientWarningPolicy() =>
        Assert.IsTrue(s_specialistPolicy.Valid, s_specialistPolicy.Detail);

    private static string PublishSpecialist(DemoDefinition lane, string property)
    {
        AotPublish.PrebuildGenerator();
        var output = Repo.PublishDirectory(lane.Project, "nativeaot-specialist");
        var outcome = AotPublish.Publish(lane.Project, output, strict: false, [property]);
        s_specialistPublish = outcome;
        s_specialistPolicy = AotPublish.ValidateWarningPolicy(lane.Demo, lane.Project, outcome.Diagnostics);
        var executable = Path.Combine(output, Repo.ExecutableName(lane.Demo));
        if (outcome.ExitCode != 0 || !File.Exists(executable))
            throw new InvalidOperationException(
                $"specialist-only NativeAOT SqlClient publish failed: exit={outcome.ExitCode}; {outcome.Diagnostics.FirstOrDefault() ?? outcome.Tail}");
        return executable;
    }
}
