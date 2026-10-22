using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>CoreWCF's server ActivitySource, managed only, with its registration on and toggled off.</summary>
[TestClass]
[TestCategory("corewcf")]
sealed class CoreWcfTests : SpanDemoLaneTests<CoreWcfTests>, ISpanDemoLane
{
    private const string TracesDisabled = "managed-wcfcore-traces-disabled";

    private static readonly DemoVariant[] Variants =
    [
        DemoVariant.Managed(DemoRuns.Managed),
        DemoVariant.Managed(TracesDisabled, ("OTEL_DOTNET_AUTO_TRACES_WCFCORE_INSTRUMENTATION_ENABLED", "false")),
    ];

    public static string LaneName => "corewcf";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants, "--disable-build-servers");

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DataRow(DemoRuns.Managed, true)]
    [DataRow(TracesDisabled, false)]
    public void DemoReport_EachRun_ReportsWhetherRegistrationIsEnabled(string run, bool registered) =>
        Assert.AreEqual(registered, Run(run).BoolField("QylRegistrationEnabled"));

    [TestMethod]
    [DataRow(DemoRuns.Managed, true)]
    [DataRow(TracesDisabled, false)]
    public void DemoReport_EachRun_ReportsWhetherRegistrationWasObserved(string run, bool registered) =>
        Assert.AreEqual(registered, Run(run).BoolField("QylRegistrationObserved"));

    [TestMethod]
    [DataRow(DemoRuns.Managed, 2)]
    [DataRow(TracesDisabled, 0)]
    public void DemoReport_EachRun_ReportsServerActivitiesOnlyWhenRegistered(string run, int activities) =>
        Assert.HasCount(activities, Run(run).Array("Activities"));
}
