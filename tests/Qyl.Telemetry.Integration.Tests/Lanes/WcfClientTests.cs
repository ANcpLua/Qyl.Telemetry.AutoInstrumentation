using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>The WCF ClientBase interceptor against an unreachable endpoint, managed only.</summary>
[TestClass]
[TestCategory("wcf-client")]
sealed class WcfClientTests : SpanDemoLaneTests<WcfClientTests>, ISpanDemoLane
{
    private const string ExpectedError = "expected-wcf-error=CommunicationException";
    private const int ReportedActivities = 2;

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed)];

    public static string LaneName => "wcf-client";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoOutput_EachRun_PrintsTheExpectedErrorTwice(string run) =>
        Assert.AreEqual(2, Run(run).TokenCount(ExpectedError));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsTwoActivities(string run) =>
        Assert.HasCount(ReportedActivities, Run(run).Array("Activities"));
}
