using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>The Azure SDK's native Azure.* sources through a failing blob client, managed and NativeAOT.</summary>
[TestClass]
[TestCategory("azure")]
sealed class AzureTests : SpanDemoLaneTests<AzureTests>, ISpanDemoLane
{
    private const string ExpectedError = "expected-azure-error=RequestFailedException";
    private const int ReportedActivities = 4;

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed), DemoVariant.NativeAot(DemoRuns.NativeAot)];

    public static string LaneName => "azure";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoOutput_EachRun_PrintsTheExpectedErrorTwice(string run) =>
        Assert.AreEqual(2, Run(run).TokenCount(ExpectedError));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsFourActivities(string run) =>
        Assert.HasCount(ReportedActivities, Run(run).Array("Activities"));
}
