using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// Microsoft.Extensions.AI, Microsoft.Agents.AI and Workflows through their UseOpenTelemetry opt-ins,
/// managed only, with each per-instrumentation toggle off in turn and all of them off together. What
/// Qyl.Telemetry.Hosting exported is checked in the report and, span by span, in the collector: what
/// reached it is exactly the spans of the subscribed sources, nothing more.
/// </summary>
[TestClass]
[TestCategory("genai")]
sealed class GenAiTests : SpanDemoLaneTests<GenAiTests>, ISpanDemoLane
{
    private const string MeaiTracesDisabled = "managed-meai-traces-disabled";
    private const string MeaiMetricsDisabled = "managed-meai-metrics-disabled";
    private const string AgentsTracesDisabled = "managed-agents-traces-disabled";
    private const string AgentsMetricsDisabled = "managed-agents-metrics-disabled";
    private const string WorkflowsTracesDisabled = "managed-workflows-traces-disabled";
    private const string AllRegistrationsDisabled = "managed-all-registrations-disabled";
    private const string MeaiSource = "Experimental.Microsoft.Extensions.AI";
    private const string AgentsSource = "Experimental.Microsoft.Agents.AI";
    private const string WorkflowsSource = "Microsoft.Agents.AI.Workflows";
    private const string MeaiTraces = "OTEL_DOTNET_AUTO_TRACES_MICROSOFTEXTENSIONSAI_INSTRUMENTATION_ENABLED";
    private const string MeaiMetrics = "OTEL_DOTNET_AUTO_METRICS_MICROSOFTEXTENSIONSAI_INSTRUMENTATION_ENABLED";
    private const string AgentsTraces = "OTEL_DOTNET_AUTO_TRACES_MICROSOFTAGENTSAI_INSTRUMENTATION_ENABLED";
    private const string AgentsMetrics = "OTEL_DOTNET_AUTO_METRICS_MICROSOFTAGENTSAI_INSTRUMENTATION_ENABLED";
    private const string WorkflowsTraces = "OTEL_DOTNET_AUTO_TRACES_MICROSOFTAGENTSAIWORKFLOWS_INSTRUMENTATION_ENABLED";
    private const string BareResponseVerified = "BareResponseVerified";
    private const string AgentResponseVerified = "AgentResponseVerified";
    private const string WorkflowOutputVerified = "WorkflowOutputVerified";
    private const int ReportedMeasurements = 14;

    private static readonly string[] Sources = [MeaiSource, AgentsSource, WorkflowsSource];
    private static readonly string[] Meters = [AgentsSource, MeaiSource];

    private static readonly Dictionary<string, (string[] Sources, string[] Meters)> Exported = new(StringComparer.Ordinal)
    {
        [DemoRuns.Managed] = (Sources, Meters),
        [MeaiTracesDisabled] = (Without(Sources, MeaiSource), Meters),
        [MeaiMetricsDisabled] = (Sources, Without(Meters, MeaiSource)),
        [AgentsTracesDisabled] = (Without(Sources, AgentsSource), Meters),
        [AgentsMetricsDisabled] = (Sources, Without(Meters, AgentsSource)),
        [WorkflowsTracesDisabled] = (Without(Sources, WorkflowsSource), Meters),
        [AllRegistrationsDisabled] = ([], []),
    };

    private static readonly DemoVariant[] Variants =
    [
        DemoVariant.Managed(DemoRuns.Managed),
        DemoVariant.Managed(MeaiTracesDisabled, (MeaiTraces, "false")),
        DemoVariant.Managed(MeaiMetricsDisabled, (MeaiMetrics, "false")),
        DemoVariant.Managed(AgentsTracesDisabled, (AgentsTraces, "false")),
        DemoVariant.Managed(AgentsMetricsDisabled, (AgentsMetrics, "false")),
        DemoVariant.Managed(WorkflowsTracesDisabled, (WorkflowsTraces, "false")),
        DemoVariant.Managed(AllRegistrationsDisabled,
            (MeaiTraces, "false"), (MeaiMetrics, "false"), (AgentsTraces, "false"), (AgentsMetrics, "false"), (WorkflowsTraces, "false")),
    ];

    public static string LaneName => "genai";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static IEnumerable<object[]> Verifications => Rows(RunKeys, BareResponseVerified, AgentResponseVerified, WorkflowOutputVerified);

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants, "--disable-build-servers");

    public static IEnumerable<JsonElement> ReportedSpans(DemoRun run) => ExportedActivities(run);

    [TestMethod]
    [DynamicData(nameof(Verifications))]
    public void DemoReport_EachRun_VerifiesEachResponse(string run, string verification) =>
        Assert.IsTrue(Run(run).BoolField(verification), Run(run).Dump());

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsActivitiesOfEverySource(string run) =>
        Assert.AreSequenceEqual(Sources,
            Run(run).Array("Activities").Select(static activity => ReportedActivity.Text(activity, "SourceName")).Distinct(StringComparer.Ordinal),
            SequenceOrder.InAnyOrder);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_PublishesBothMeters(string run) =>
        Assert.AreSequenceEqual(Meters.Order(StringComparer.Ordinal), Run(run).Strings("PublishedMeterNames"));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsFourteenMeasurements(string run) =>
        Assert.HasCount(ReportedMeasurements, Run(run).Array("Measurements"));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void HostingExport_EachToggle_SubscribesExactlyTheEnabledSources(string run) =>
        Assert.AreSequenceEqual(Exported[run].Sources.Order(StringComparer.Ordinal), Run(run).Strings("QylExportedActivitySources"));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void HostingExport_EachToggle_SubscribesExactlyTheEnabledMeters(string run) =>
        Assert.AreSequenceEqual(Exported[run].Meters,
            Run(run).Strings("QylExportedMeterNames").Where(static meter => Meters.Contains(meter)).Distinct(StringComparer.Ordinal),
            SequenceOrder.InAnyOrder);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void CapturedMetrics_EachToggle_ComeFromExactlyTheEnabledMeters(string run) =>
        Assert.AreSequenceEqual(Exported[run].Meters,
            Run(run).Telemetry.Metrics.Select(static metric => metric.Scope?.Name).OfType<string>()
                .Where(static meter => Meters.Contains(meter)).Distinct(StringComparer.Ordinal),
            SequenceOrder.InAnyOrder);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void CapturedSpans_EachToggle_AreExactlyTheEnabledSourcesSpans(string run) =>
        Assert.AreSequenceEqual(ExportedActivities(Run(run)).Select(static activity => ReportedActivity.Text(activity, "SpanId")),
            CapturedTelemetry.ServiceSpanIds(Run(run), Lane.ServiceName), SequenceOrder.InAnyOrder);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void CapturedMetrics_EachToggle_HoldEveryEnabledMeasurement(string run)
    {
        var measurements = Run(run).Array("Measurements")
            .Where(measurement => Exported[run].Meters.Contains(ReportedActivity.Text(measurement, "MeterName")));
        var unmatched = CapturedTelemetry.UnmatchedMetrics(Run(run), Lane.ServiceName, LaneReport.Metrics(measurements, "InstrumentName"));

        Assert.IsEmpty(unmatched, string.Join("\n", unmatched));
    }

    private static JsonElement[] ExportedActivities(DemoRun run) =>
        [.. run.Array("Activities").Where(activity => Exported[run.Key].Sources.Contains(ReportedActivity.Text(activity, "SourceName")))];

    private static string[] Without(string[] values, string removed) => [.. values.Where(value => value != removed)];
}
