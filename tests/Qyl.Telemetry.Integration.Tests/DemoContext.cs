using System.Text.Json;
using System.Text.Json.Serialization;
using Qyl.Telemetry.Tests;

namespace Qyl.Telemetry.Integration.Tests;

/// <summary>Builds and runs the real demo application; each execution owns its OTLP receiver.</summary>
sealed class DemoContext
{
    private readonly TestContext _testContext;

    public DemoContext(DemoDefinition lane, TestContext testContext)
    {
        Lane = lane;
        _testContext = testContext;
        Telemetry = new CapturedTelemetryCheck(lane.ServiceName);
        var environment = Shell.CleanEnvironment();
        if (lane.Services.Count > 0)
        {
            var testbed = Testbed.Load();
            foreach (var service in lane.Services)
            {
                testbed.Wake(service);
                foreach (var (key, value) in testbed.DemoEnvironment(service))
                    environment[key] = value;
            }
        }

        Environment = environment;
    }

    public DemoDefinition Lane { get; }
    public CapturedTelemetryCheck Telemetry { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }

    public IReadOnlyDictionary<string, string> With(params (string Key, string Value)[] overrides) =>
        Environment.With(overrides.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));

    public void BuildManaged(params string[] extra)
    {
        DotNet.Build(Lane.Project, extra);
        var assembly = Repo.BinAssembly(Lane.Project);
        Assert.IsTrue(File.Exists(assembly), $"managed assembly missing: {assembly}");
    }

    public string PublishNativeAot() => AotPublish.PublishDemo(Lane.Demo);

    public Task<DemoRun> RunManagedAsync(string label, IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? arguments = null) =>
        ExecuteAsync(label, LaneRuntime.Managed, "dotnet", [Repo.BinAssembly(Lane.Project), .. arguments ?? []],
            Lane.Project.Parent, environment);

    public Task<DemoRun> RunNativeAotAsync(string executable, string label,
        IReadOnlyDictionary<string, string>? environment = null, IReadOnlyList<string>? arguments = null) =>
        ExecuteAsync(label, LaneRuntime.NativeAot, executable, arguments ?? [], Path.GetDirectoryName(executable)!, environment);

    public RunningProcess StartManaged(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment) =>
        RunningProcess.Start("dotnet", [Repo.BinAssembly(Lane.Project), .. arguments], Lane.Project.Parent, environment);

    public void RecordCapture(string label, TelemetrySnapshot snapshot, ProcessResult? process = null)
    {
        var directory = Path.Combine(Repo.Artifacts, "integration-tests", Lane.Name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        var registry = snapshot is { Spans.Count: 0, Logs.Count: 0, Metrics.Count: 0 }
            ? null
            : RegistryConformance.Check(snapshot);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Label = label, Capture = snapshot, Registry = registry, Process = process },
            new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        _testContext.AddResultFile(path);
        _testContext.WriteLine($"{label}: {snapshot.Spans.Count} spans, {snapshot.Logs.Count} logs, {snapshot.Metrics.Count} metrics; full capture: {path}");
        Assert.IsTrue(snapshot.Receipts.All(static receipt => receipt is { Rejected: 0, Error: null }),
            $"{label}: the receiver rejected telemetry; see {path}");
        if (registry is not null)
            Assert.IsTrue(registry.IsConformant, $"{label}: registry violations; see {path}");
    }

    private async Task<DemoRun> ExecuteAsync(string label, LaneRuntime runtime, string fileName,
        IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string>? environment)
    {
        await using var receiver = await OtlpReceiver.StartAsync(_testContext.CancellationToken);
        var variables = (environment ?? Environment).With(new Dictionary<string, string>
        {
            ["QYL_ENDPOINT"] = receiver.Endpoint.ToString().TrimEnd('/'),
        });
        using var process = RunningProcess.Start(fileName, arguments, workingDirectory, variables);
        var result = await process.WaitForExitAsync(TimeSpan.FromMinutes(10), _testContext.CancellationToken);
        // The joined process has flushed/disposed its exporters. Every accepted request is retained;
        // no wall-clock window or settling sleep selects the records used by these assertions.
        var snapshot = receiver.Snapshot();
        RecordCapture(label, snapshot, result);
        return new DemoRun(label, runtime, result, snapshot);
    }
}
