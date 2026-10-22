namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>The ASP.NET Core component meters, managed and NativeAOT (a vendor-warned publish).</summary>
[TestClass]
[TestCategory("aspnetcore-metrics")]
sealed class AspNetCoreMetricsTests : DemoLaneTests<AspNetCoreMetricsTests>, IDemoLane
{
    private const int ReportedMetricSeries = 3;

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed), DemoVariant.NativeAot(DemoRuns.NativeAot)];

    public static string LaneName => "aspnetcore-metrics";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsAtLeastThreeMetricSeries(string run) =>
        Assert.IsGreaterThanOrEqualTo(ReportedMetricSeries, Run(run).Array("Metrics").Length);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ReportedMetrics_EachRun_ReachTheCapture(string run)
    {
        var unmatched = CapturedTelemetry.UnmatchedMetrics(Run(run), Lane.ServiceName, LaneReport.Metrics(Run(run).Array("Metrics")));

        Assert.IsEmpty(unmatched, string.Join("\n", unmatched));
    }
}
