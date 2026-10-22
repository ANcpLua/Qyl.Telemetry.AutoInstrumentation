namespace Qyl.Telemetry.Integration.Tests.Lanes;

/// <summary>
/// AddQyl() registers the OpenTelemetry ILogger provider, so an ILogger call becomes a LogRecord and
/// never a qyl Activity. The global logs control unset (six records exported) and false (provider
/// unregistered, nothing exported), managed and NativeAOT. Six records stay below the batch limit, and
/// periodic export is delayed so the first HTTP/protobuf request must be made by provider disposal,
/// covering the SDK 1.20.0 shutdown fix.
/// </summary>
[TestClass]
[TestCategory("ilogger")]
sealed class LoggerTests : DemoLaneTests<LoggerTests>, IDemoLane
{
    private const string ManagedLogsDisabled = "managed-logs-disabled";
    private const string NativeAotLogsDisabled = "nativeaot-logs-disabled";
    private const string RecordCountToken = "ilogger-record-count=";

    private static readonly (string, string) DelayedPeriodicExport = ("OTEL_BLRP_SCHEDULE_DELAY", "3600000");
    private static readonly (string, string) LogsDisabled = ("OTEL_DOTNET_AUTO_LOGS_INSTRUMENTATION_ENABLED", "false");

    private static readonly (string Severity, string? Body)[] Records =
    [
        ("Trace", "qyl trace record"),
        ("Debug", "qyl debug record"),
        ("Information", "qyl information record"),
        ("Warning", "qyl warning record"),
        ("Error", "qyl error record"),
        ("Critical", "qyl critical record"),
    ];

    private static readonly DemoVariant[] Variants =
    [
        DemoVariant.Managed(DemoRuns.Managed, DelayedPeriodicExport),
        DemoVariant.Managed(ManagedLogsDisabled, LogsDisabled, DelayedPeriodicExport),
        DemoVariant.NativeAot(DemoRuns.NativeAot, DelayedPeriodicExport),
        DemoVariant.NativeAot(NativeAotLogsDisabled, LogsDisabled, DelayedPeriodicExport),
    ];

    public static string LaneName => "ilogger";

    public static IReadOnlyList<string> RunKeys => [.. Variants.Select(static variant => variant.Key)];

    public static Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane) => lane.RunAllAsync(Variants);

    [TestMethod]
    [DataRow(DemoRuns.Managed, true)]
    [DataRow(ManagedLogsDisabled, false)]
    [DataRow(DemoRuns.NativeAot, true)]
    [DataRow(NativeAotLogsDisabled, false)]
    public void DemoOutput_EachRun_PrintsItsRecordCount(string run, bool logsEnabled) =>
        Assert.Contains(RecordCountToken + ExpectedRecords(logsEnabled).Length, Run(run).Stdout, StringComparison.Ordinal);

    [TestMethod]
    [DataRow(DemoRuns.Managed, true)]
    [DataRow(ManagedLogsDisabled, false)]
    [DataRow(DemoRuns.NativeAot, true)]
    [DataRow(NativeAotLogsDisabled, false)]
    public void DemoReport_EachRun_ReportsTheLogsControl(string run, bool logsEnabled) =>
        Assert.AreEqual(logsEnabled, Run(run).BoolField("LogsControlEnabled"));

    [TestMethod]
    [DataRow(DemoRuns.Managed, true)]
    [DataRow(ManagedLogsDisabled, false)]
    [DataRow(DemoRuns.NativeAot, true)]
    [DataRow(NativeAotLogsDisabled, false)]
    public void DemoReport_EachRun_ReportsTheLoggerProviderRegistration(string run, bool logsEnabled) =>
        Assert.AreEqual(logsEnabled, Run(run).BoolField("OpenTelemetryLoggerProviderRegistered"));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_ReportsNoQylActivityForLogCalls(string run) =>
        Assert.IsEmpty(Run(run).Array("QylActivities"));

    [TestMethod]
    [DataRow(DemoRuns.Managed, true)]
    [DataRow(ManagedLogsDisabled, false)]
    [DataRow(DemoRuns.NativeAot, true)]
    [DataRow(NativeAotLogsDisabled, false)]
    public void DemoReport_EachRun_ReportsEveryRecordOnlyWhenEnabled(string run, bool logsEnabled) =>
        Assert.AreSequenceEqual(ExpectedRecords(logsEnabled),
            Run(run).Array("Records").Select(static record => (ReportedActivity.Text(record, "Severity") ?? "absent", ReportedActivity.Text(record, "Body"))));

    [TestMethod]
    [DataRow(DemoRuns.Managed, true)]
    [DataRow(ManagedLogsDisabled, false)]
    [DataRow(DemoRuns.NativeAot, true)]
    [DataRow(NativeAotLogsDisabled, false)]
    public void CapturedLogs_EachRun_HoldEveryRecordOnlyWhenEnabled(string run, bool logsEnabled) =>
        Assert.AreSequenceEqual(ExpectedRecords(logsEnabled), CapturedTelemetry.ServiceLogs(Run(run), Lane.ServiceName));

    private static (string Severity, string? Body)[] ExpectedRecords(bool logsEnabled) => logsEnabled ? Records : [];
}
