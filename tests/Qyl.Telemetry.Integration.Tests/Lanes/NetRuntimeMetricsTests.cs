namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>The System.Runtime meter and the process instruments, managed and NativeAOT.</summary>
[TestClass]
[TestCategory("netruntime-metrics")]
sealed class NetRuntimeMetricsTests : DemoLaneTests<NetRuntimeMetricsTests>, IDemoLane
{
    private const int ReportedMetricSeries = 6;

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed), DemoVariant.NativeAot(DemoRuns.NativeAot)];

    public static string LaneName => "netruntime-metrics";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsAtLeastSixMetricSeries(string run) =>
        Assert.IsGreaterThanOrEqualTo(ReportedMetricSeries, Run(run).Array("Metrics").Length);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ReportedMetrics_EachRun_ReachTheCapture(string run)
    {
        var unmatched = CapturedTelemetry.UnmatchedMetrics(Run(run), Lane.ServiceName, LaneReport.Metrics(Run(run).Array("Metrics")));

        Assert.IsEmpty(unmatched, string.Join("\n", unmatched));
    }
}
