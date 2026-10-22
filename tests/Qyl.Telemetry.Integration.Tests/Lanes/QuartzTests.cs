using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// Quartz's native source and its native Meter, managed: one Quartz.Job.Execute span per scheduled job
/// (the job-store calls on the same source are not reported), and the job duration series switched by
/// the Quartz metrics toggle, the global metrics toggle and the per-instrumentation override.
/// </summary>
[TestClass]
[TestCategory("quartz")]
sealed class QuartzTests : SpanDemoLaneTests<QuartzTests>, ISpanDemoLane
{
    private const string QuartzMetricsDisabled = "managed-quartz-metrics-disabled";
    private const string AllMetricsDisabled = "managed-all-metrics-disabled";
    private const string QuartzMetricsOverride = "managed-quartz-metrics-override";
    private const string SchedulerFired = "scheduler-fired=true";
    private const string JobDuration = "quartz.job.execution.duration";
    private const int JobExecuteActivities = 2;

    private static readonly (string, string) QuartzMetricsOff = ("OTEL_DOTNET_AUTO_METRICS_QUARTZ_INSTRUMENTATION_ENABLED", "false");
    private static readonly (string, string) QuartzMetricsOn = ("OTEL_DOTNET_AUTO_METRICS_QUARTZ_INSTRUMENTATION_ENABLED", "true");
    private static readonly (string, string) AllMetricsOff = ("OTEL_DOTNET_AUTO_METRICS_INSTRUMENTATION_ENABLED", "false");
    private static readonly string[] ExpectNoQuartzMetrics = ["--expect-no-quartz-metrics"];

    private static readonly DemoVariant[] Variants =
    [
        DemoVariant.Managed(DemoRuns.Managed),
        DemoVariant.Managed(QuartzMetricsDisabled, QuartzMetricsOff) with { Arguments = ExpectNoQuartzMetrics },
        DemoVariant.Managed(AllMetricsDisabled, AllMetricsOff) with { Arguments = ExpectNoQuartzMetrics },
        DemoVariant.Managed(QuartzMetricsOverride, AllMetricsOff, QuartzMetricsOn),
    ];

    public static string LaneName => "quartz";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, SchedulerFired);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(OutcomeTokens))]
    public void DemoOutput_EachRun_PrintsOutcomeToken(string run, string token) =>
        Assert.Contains(token, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsOneActivityPerScheduledJob(string run) =>
        Assert.HasCount(JobExecuteActivities, Run(run).Array("Activities"));

    [TestMethod]
    [DataRow(DemoRuns.Managed, true)]
    [DataRow(QuartzMetricsDisabled, false)]
    [DataRow(AllMetricsDisabled, false)]
    [DataRow(QuartzMetricsOverride, true)]
    public void DemoReport_EachToggle_ReportsWhetherMetricsAreExpected(string run, bool metricsEnabled) =>
        Assert.AreEqual(metricsEnabled, Run(run).BoolField("ExpectedMetricsEnabled"));

    [TestMethod]
    [DataRow(DemoRuns.Managed, 2)]
    [DataRow(QuartzMetricsDisabled, 0)]
    [DataRow(AllMetricsDisabled, 0)]
    [DataRow(QuartzMetricsOverride, 2)]
    public void DemoReport_EachToggle_ReportsOneDurationSeriesPerJobOnlyWhenEnabled(string run, int series) =>
        Assert.HasCount(series, Run(run).Array("JobMetrics"));

    [TestMethod]
    [DataRow(DemoRuns.Managed)]
    [DataRow(QuartzMetricsOverride)]
    public void CapturedMetrics_MetricsEnabled_HoldTheJobDuration(string run)
    {
        var unmatched = CapturedTelemetry.UnmatchedMetrics(Run(run), Lane.ServiceName, [ReportedMetric.Named(JobDuration)]);

        Assert.IsEmpty(unmatched, string.Join("\n", unmatched));
    }

    [TestMethod]
    [DataRow(QuartzMetricsDisabled)]
    [DataRow(AllMetricsDisabled)]
    public void CapturedMetrics_MetricsDisabled_HoldNoJobDuration(string run) =>
        Assert.DoesNotContain(JobDuration, CapturedTelemetry.MetricNames(Run(run)));
}
