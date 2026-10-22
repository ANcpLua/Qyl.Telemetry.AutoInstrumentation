using System.Text.Json;
using System.Text.Json.Serialization;
using Qyl.Telemetry.Tests;

namespace Qyl.Telemetry.Integration.Tests;

/// <summary>One demo execution a lane runs: its key, runtime, environment overrides and arguments.</summary>
sealed record DemoVariant(string Key, LaneRuntime Runtime, IReadOnlyList<(string Key, string Value)> Overrides, IReadOnlyList<string> Arguments)
{
    public static DemoVariant Managed(string key, params (string Key, string Value)[] overrides) => new(key, LaneRuntime.Managed, overrides, []);

    public static DemoVariant NativeAot(string key, params (string Key, string Value)[] overrides) => new(key, LaneRuntime.NativeAot, overrides, []);
}

/// <summary>
/// Builds and runs the real demo application; each execution owns its OTLP receiver. It builds, runs and
/// records; the lane tests assert on what it returns.
/// </summary>
sealed class DemoContext
{
    private readonly TestContext _testContext;

    public DemoContext(DemoDefinition lane, TestContext testContext)
    {
        Lane = lane;
        _testContext = testContext;
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
    public IReadOnlyDictionary<string, string> Environment { get; }
    public CancellationToken CancellationToken => _testContext.CancellationToken;

    public IReadOnlyDictionary<string, string> With(params IEnumerable<(string Key, string Value)> overrides) =>
        Environment.With(overrides.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));

    public void BuildManaged(params string[] extra)
    {
        DotNet.Build(Lane.Project, extra);
        var assembly = Repo.BinAssembly(Lane.Project);
        if (!File.Exists(assembly))
            throw new FileNotFoundException($"{Lane.Name}: the managed build left no assembly", assembly);
    }

    public string PublishNativeAot() => AotPublish.PublishDemo(Lane.Demo);

    /// <summary>Builds the managed demo, publishes NativeAOT once when a variant needs it, and runs every variant in order.</summary>
    public async Task<IReadOnlyDictionary<string, DemoRun>> RunAllAsync(IReadOnlyList<DemoVariant> variants, params string[] buildArguments)
    {
        BuildManaged(buildArguments);
        var executable = variants.Any(static variant => variant.Runtime is LaneRuntime.NativeAot) ? PublishNativeAot() : null;
        var runs = new Dictionary<string, DemoRun>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            var environment = With(variant.Overrides);
            runs[variant.Key] = variant.Runtime is LaneRuntime.NativeAot
                ? await RunNativeAotAsync(executable!, variant.Key, environment, variant.Arguments)
                : await RunManagedAsync(variant.Key, environment, variant.Arguments);
        }

        return runs;
    }

    public Task<DemoRun> RunManagedAsync(string key, IReadOnlyDictionary<string, string> environment, IReadOnlyList<string> arguments) =>
        ExecuteAsync(key, LaneRuntime.Managed, "dotnet", [Repo.BinAssembly(Lane.Project), .. arguments], Lane.Project.Parent, environment);

    public Task<DemoRun> RunNativeAotAsync(string executable, string key, IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> arguments) =>
        ExecuteAsync(key, LaneRuntime.NativeAot, executable, arguments, Path.GetDirectoryName(executable)!, environment);

    public RunningProcess StartManaged(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment) =>
        RunningProcess.Start("dotnet", [Repo.BinAssembly(Lane.Project), .. arguments], Lane.Project.Parent, environment);

    /// <summary>Writes the complete capture and its registry report next to the run and attaches the file.</summary>
    public DemoCapture RecordCapture(string label, TelemetrySnapshot snapshot, params ProcessResult[] processes)
    {
        var directory = Path.Combine(Repo.Artifacts, "integration-tests", Lane.Name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        var registry = snapshot is { Spans.Count: 0, Logs.Count: 0, Metrics.Count: 0 }
            ? null
            : RegistryConformance.Check(snapshot);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Label = label, Capture = snapshot, Registry = registry, Processes = processes },
            new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        _testContext.AddResultFile(path);
        _testContext.WriteLine($"{label}: {snapshot.Spans.Count} spans, {snapshot.Logs.Count} logs, {snapshot.Metrics.Count} metrics; full capture: {path}");
        return new DemoCapture(snapshot, registry, path);
    }

    private async Task<DemoRun> ExecuteAsync(string key, LaneRuntime runtime, string fileName,
        IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        await using var receiver = await OtlpReceiver.StartAsync(CancellationToken);
        var variables = environment.With(new Dictionary<string, string>
        {
            ["QYL_ENDPOINT"] = receiver.Endpoint.ToString().TrimEnd('/'),
        });
        using var process = RunningProcess.Start(fileName, arguments, workingDirectory, variables);
        var result = await process.WaitForExitAsync(TimeSpan.FromMinutes(10), CancellationToken);
        // The joined process has flushed/disposed its exporters. Every accepted request is retained;
        // no wall-clock window or settling sleep selects the records the lanes assert on.
        return new DemoRun(Lane.Name, key, runtime, result, RecordCapture($"{Lane.Name} {key}", receiver.Snapshot(), result));
    }
}
