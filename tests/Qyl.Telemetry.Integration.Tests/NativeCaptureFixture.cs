using System.Text.Json;
using Qyl.Telemetry.AutoInstrumentation;
using Qyl.Telemetry.TestFixtures;
using Qyl.Telemetry.Tests.Capture;

namespace Qyl.Telemetry.Integration.Tests;

/// <summary>One row of a native capture suite: the capture configuration and the outcomes the child process must verify.</summary>
sealed record NativeCaptureCase(
    string Profile,
    string? RequestHeaders,
    string? ResponseHeaders,
    ClientCaptureControl Control,
    bool RequestCaptured,
    bool ResponseCaptured,
    string? HttpRequestHeaders = null)
{
    public ClientCaptureScenario Scenario => new(Profile, Control, RequestCaptured, ResponseCaptured);
}

/// <summary>A finished native capture process and the status lines it printed.</summary>
sealed record NativeCaptureRun(ProcessResult Process)
{
    public IReadOnlyList<string> StatusLines =>
        [.. Process.Stdout.Split('\n').Where(static line => line.StartsWith("{\"status\":", StringComparison.Ordinal))];

    /// <summary>The single status line's root element; throws when the process printed none or several.</summary>
    public JsonElement Status
    {
        get
        {
            using var document = JsonDocument.Parse(StatusLines.Single());
            return document.RootElement.Clone();
        }
    }
}

/// <summary>One class-owned native publication, with a separate child process and evidence for each row.</summary>
internal sealed class NativeCaptureFixture(string executable, string output)
{
    internal static async Task<NativeCaptureFixture> PublishAsync(string fixture, TestContext context)
    {
        var project = Repo.Fixtures / fixture / (fixture + ".csproj");
        var directory = Repo.Artifacts / "captured-fields" / fixture / Guid.NewGuid().ToString("N");
        var executable = await DotNet.PublishNativeAotAsync(project, directory, context.CancellationToken,
            ["-p:TreatWarningsAsErrors=true"]);
        context.WriteLine($"Native fixture published once for this class: {directory}");
        return new NativeCaptureFixture(executable, directory);
    }

    internal static Dictionary<string, string> CreateEnvironment(ClientCaptureControl control, string instrumentation)
    {
        var environment = Shell.CleanEnvironment();
        if (control is ClientCaptureControl.InstrumentationDisabled)
            environment[$"OTEL_DOTNET_AUTO_TRACES_{instrumentation}_INSTRUMENTATION_ENABLED"] = "false";
        if (control is ClientCaptureControl.GlobalDisabled)
            environment[QylAutoInstrumentationOptions.GlobalEnabledVariable] = "false";
        return environment;
    }

    /// <summary>Runs one row's child process to its exit and attaches its stdout and stderr.</summary>
    internal async Task<NativeCaptureRun> RunAsync(ClientCaptureScenario scenario, IReadOnlyDictionary<string, string> environment, TestContext context)
    {
        var profile = scenario.Profile;
        context.CancellationToken.ThrowIfCancellationRequested();
        using var process = RunningProcess.Start(executable, scenario.ToArguments(), Repo.Root, environment);
        var run = await process.WaitForExitAsync(TimeSpan.FromMinutes(1), context.CancellationToken);
        var stdout = Path.Combine(output, profile + ".stdout");
        var stderr = Path.Combine(output, profile + ".stderr");
        await File.WriteAllTextAsync(stdout, run.Stdout, context.CancellationToken);
        await File.WriteAllTextAsync(stderr, run.Stderr, context.CancellationToken);
        context.AddResultFile(stdout);
        context.AddResultFile(stderr);
        return new NativeCaptureRun(run);
    }
}

/// <summary>A native capture suite: its fixture, instrumentation, configuration variables and rows.</summary>
interface INativeCaptureSuite
{
    static abstract string Fixture { get; }
    static abstract string Instrumentation { get; }
    static abstract string RequestVariable { get; }
    static abstract string ResponseVariable { get; }
    static abstract IReadOnlyList<NativeCaptureCase> Cases { get; }
}

/// <summary>
/// Publishes a native capture fixture once per class and runs every row once; the child process verifies
/// the captured headers, the controls and the span parentage in-process and prints its verdict, which
/// the tests read row by row.
/// </summary>
abstract class NativeCaptureTests<TSuite> where TSuite : INativeCaptureSuite
{
    private static IReadOnlyDictionary<string, NativeCaptureRun>? s_runs;

    public static IEnumerable<object[]> Profiles => TSuite.Cases.Select(static row => new object[] { row.Profile });

    [ClassInitialize(InheritanceBehavior.BeforeEachDerivedClass)]
    public static async Task PublishAndRunAsync(TestContext context)
    {
        var fixture = await NativeCaptureFixture.PublishAsync(TSuite.Fixture, context);
        var runs = new Dictionary<string, NativeCaptureRun>(StringComparer.Ordinal);
        foreach (var row in TSuite.Cases)
            runs[row.Profile] = await fixture.RunAsync(row.Scenario, Environment(row), context);
        s_runs = runs;
    }

    private static NativeCaptureRun Run(string profile) =>
        (s_runs ?? throw new InvalidOperationException($"{TSuite.Fixture}: the rows have not run"))[profile];

    private static Dictionary<string, string> Environment(NativeCaptureCase row)
    {
        var environment = NativeCaptureFixture.CreateEnvironment(row.Control, TSuite.Instrumentation);
        if (row.RequestHeaders is not null)
            environment[TSuite.RequestVariable] = row.RequestHeaders;
        if (row.ResponseHeaders is not null)
            environment[TSuite.ResponseVariable] = row.ResponseHeaders;
        if (row.HttpRequestHeaders is not null)
            environment[QylAutoInstrumentationOptions.HttpClientRequestHeadersVariable] = row.HttpRequestHeaders;
        return environment;
    }

    [TestMethod]
    [DynamicData(nameof(Profiles))]
    public void NativeCapture_EachProfile_ExitsWithZero(string profile) =>
        Assert.AreEqual(0, Run(profile).Process.ExitCode, Run(profile).Process.Describe());

    [TestMethod]
    [DynamicData(nameof(Profiles))]
    public void NativeCapture_EachProfile_WritesNothingToStderr(string profile) =>
        Assert.IsEmpty(Run(profile).Process.Stderr);

    [TestMethod]
    [DynamicData(nameof(Profiles))]
    public void NativeCapture_EachProfile_PrintsOneStatusLine(string profile) =>
        Assert.ContainsSingle(Run(profile).StatusLines, Run(profile).Process.Stdout);

    [TestMethod]
    [DynamicData(nameof(Profiles))]
    public void NativeCapture_EachProfile_VerifiesHeadersControlsAndParentage(string profile) =>
        Assert.AreEqual("passed", Run(profile).Status.GetProperty("status").GetString(), Run(profile).Process.Stdout);

    [TestMethod]
    [DynamicData(nameof(Profiles))]
    public void NativeCapture_EachProfile_ReportsItsProfile(string profile) =>
        Assert.AreEqual(profile, Run(profile).Status.GetProperty("profile").GetString());

    [TestMethod]
    [DynamicData(nameof(Profiles))]
    public void NativeCapture_EachProfile_ReportsTheCaptureSchemaVersion(string profile) =>
        Assert.AreEqual(ActivityCaptureReport.SchemaVersion, Run(profile).Status.GetProperty("schema_version").GetInt32());

    [TestMethod]
    [DynamicData(nameof(Profiles))]
    public void NativeCapture_EachProfile_RunsWithoutDynamicCode(string profile) =>
        Assert.IsFalse(Run(profile).Status.GetProperty("dynamicCodeSupported").GetBoolean());
}
