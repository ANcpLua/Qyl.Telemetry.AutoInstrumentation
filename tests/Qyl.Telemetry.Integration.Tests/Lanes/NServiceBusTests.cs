using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// NServiceBus traces its own pipeline: the publish, the routed send, the send that cannot be routed,
/// the incoming "process message" span and the handler invocation beneath it. Managed only.
/// </summary>
[TestClass]
[TestCategory("nservicebus")]
sealed class NServiceBusTests : SpanDemoLaneTests<NServiceBusTests>, ISpanDemoLane
{
    private const string Published = "published=alpha";
    private const string SentAndHandled = "sent-and-handled=beta";
    private const string ExpectedError = "expected-nservicebus-error=no-route";
    private const int ReportedActivities = 5;

    private static readonly DemoVariant[] Variants = [DemoVariant.Managed(DemoRuns.Managed)];

    public static string LaneName => "nservicebus";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> OutcomeTokens => Rows(RunKeys, Published, SentAndHandled, ExpectedError);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => run.Array("Activities");

    [TestMethod]
    [DynamicData(nameof(OutcomeTokens))]
    public void DemoOutput_EachRun_PrintsOutcomeToken(string run, string token) =>
        Assert.Contains(token, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsFiveActivities(string run) =>
        Assert.HasCount(ReportedActivities, Run(run).Array("Activities"));
}
