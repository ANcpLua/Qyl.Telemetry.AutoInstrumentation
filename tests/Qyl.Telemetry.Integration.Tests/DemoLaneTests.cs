using System.Text.Json;

namespace Qyl.Telemetry.Integration.Tests;

/// <summary>The run keys the lanes share.</summary>
static class DemoRuns
{
    public const string Managed = "managed";
    public const string NativeAot = "nativeaot";
}

/// <summary>A demo lane: which registered demo it is, the runs it makes and how it makes them.</summary>
interface IDemoLane
{
    static abstract string LaneName { get; }

    /// <summary>Every run key, known before the lane runs, so MSTest can enumerate the rows.</summary>
    static abstract IReadOnlyList<string> RunKeys { get; }

    /// <summary>Runs whose failure is the pinned behavior; the success checks skip them and the lane asserts them itself.</summary>
    static virtual IReadOnlyList<string> PinnedFailures => [];

    static abstract Task<IReadOnlyDictionary<string, DemoRun>> RunAsync(DemoContext lane);
}

/// <summary>A demo lane whose report names spans the capture must hold unchanged.</summary>
interface ISpanDemoLane : IDemoLane
{
    static abstract IEnumerable<JsonElement> ReportedSpans(DemoRun run);
}

/// <summary>
/// Runs a lane's demo once per class and holds the runs; every test of the lane reads them. The checks every
/// lane shares are rows over its run keys: the process, the report's own verdict and the capture.
/// </summary>
abstract class DemoLaneTests<TLane> where TLane : IDemoLane
{
    private static IReadOnlyDictionary<string, DemoRun>? s_runs;

    protected static DemoDefinition Lane => DemoLanes.ByName(TLane.LaneName);

    public static IEnumerable<object[]> AllRuns => TLane.RunKeys.Select(static key => new object[] { key });

    public static IEnumerable<object[]> SucceedingRuns =>
        TLane.RunKeys.Except(TLane.PinnedFailures, StringComparer.Ordinal).Select(static key => new object[] { key });

    [ClassInitialize(InheritanceBehavior.BeforeEachDerivedClass)]
    public static async Task RunLaneAsync(TestContext context) =>
        s_runs = await TLane.RunAsync(new DemoContext(Lane, context));

    protected static DemoRun Run(string key) =>
        (s_runs ?? throw new InvalidOperationException($"{TLane.LaneName}: the lane has not run"))[key];

    protected static IEnumerable<object[]> Rows(IEnumerable<string> runs, params IEnumerable<string> values) =>
        runs.SelectMany(run => values.Select(value => new object[] { run, value }));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoProcess_EachRun_ExitsWithZero(string run) =>
        Assert.AreEqual(0, Run(run).Process.ExitCode, Run(run).Process.Describe());

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoProcess_EachRun_WritesNothingToStderr(string run) =>
        Assert.AreEqual(string.Empty, Run(run).Process.Stderr);

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_NamesItsRuntimeMode(string run) =>
        Assert.AreEqual(Run(run).ExpectedRuntimeMode, Run(run).StringField("RuntimeMode"));

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void DemoReport_EachRun_Passes(string run) =>
        Assert.IsTrue(Run(run).BoolField("Pass"), Run(run).Dump());

    [TestMethod]
    [DynamicData(nameof(AllRuns))]
    public void OtlpReceiver_EachRun_AcceptsEveryExport(string run) =>
        Assert.DoesNotContain(static receipt => receipt is not { Rejected: 0, Error: null }, Run(run).Telemetry.Receipts,
            $"see {Run(run).Capture.Path}");

    [TestMethod]
    [DynamicData(nameof(AllRuns))]
    public void OtlpCapture_EachRun_ConformsToTheRegistry(string run) =>
        Assert.IsTrue(Run(run).Capture.Registry?.IsConformant ?? true, $"see {Run(run).Capture.Path}");
}

/// <summary>A lane whose reported spans must reach the capture exactly once and unchanged.</summary>
abstract class SpanDemoLaneTests<TLane> : DemoLaneTests<TLane> where TLane : ISpanDemoLane
{
    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ReportedSpans_EachRun_AreExportedExactlyOnce(string run)
    {
        var miscounted = CapturedTelemetry.SpanExportCounts(Run(run), TLane.ReportedSpans(Run(run)));

        Assert.IsEmpty(miscounted, string.Join("\n", miscounted));
    }

    [TestMethod]
    [DynamicData(nameof(SucceedingRuns))]
    public void ReportedSpans_EachRun_AreExportedUnchanged(string run)
    {
        var differences = CapturedTelemetry.SpanDifferences(Run(run), TLane.ReportedSpans(Run(run)));

        Assert.IsEmpty(differences, string.Join("\n", differences));
    }
}
