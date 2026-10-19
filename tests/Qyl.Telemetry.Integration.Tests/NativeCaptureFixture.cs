using System.Text.Json;
using Qyl.Telemetry.AutoInstrumentation;
using Qyl.Telemetry.TestFixtures;
using Qyl.Telemetry.Tests.Capture;

namespace Qyl.Telemetry.Integration.Tests;

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

    internal async Task RunAsync(ClientCaptureScenario scenario, IReadOnlyDictionary<string, string> environment, TestContext context)
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
        Assert.AreEqual(0, run.ExitCode, run.Describe());
        using var document = JsonDocument.Parse(run.Stdout.Split('\n')
            .Single(static line => line.StartsWith("{\"status\":", StringComparison.Ordinal)));
        Assert.AreEqual("passed", document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(profile, document.RootElement.GetProperty("profile").GetString());
        Assert.AreEqual(ActivityCaptureReport.SchemaVersion, document.RootElement.GetProperty("schema_version").GetInt32());
        Assert.IsFalse(document.RootElement.GetProperty("dynamicCodeSupported").GetBoolean());
        Assert.IsEmpty(run.Stderr);
    }
}
