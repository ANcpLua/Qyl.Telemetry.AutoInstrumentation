namespace Qyl.Build;

sealed partial class QylBuild
{
    [Parameter("Demos: the lanes to run, by name (default: all thirty) — e.g. --lane quartz http-client", Name = "Lane")]
    readonly string[] _lane = [];

    Target Demos => definition => definition
        .Description("Native MSTest integration cases: real managed/NativeAOT applications, independent reports and complete OTLP captures")
        .After(AotPublishGate)
        .Executes(() =>
        {
            var lanes = _lane.Length == 0 ? DemoLanes.All : _lane.Select(DemoLanes.ByName).ToArray();
            var results = Repo.Artifacts / "integration-results" / Guid.NewGuid().ToString("N");
            results.CreateDirectory();
            var filter = _lane.Length == 0 ? Array.Empty<string>() : new[] { "--filter", string.Join('|', lanes.Select(static lane => "TestCategory=" + lane.Name)) };
            // Every lane runs at least ten cases; the smallest, wcf-client, runs exactly ten. Full
            // discovery also includes twenty-nine representation, twenty-two transport, eighty-six
            // generator and twelve host-configuration cases. Missing that coverage must fail the run.
            var minimum = lanes.Count * 10 + (_lane.Length == 0 ? 149 : 0);
            Shell.Checked("dotnet", ["run", "--project", Repo.IntegrationTests, "-c", "Release", "--",
                "--report-trx", "--results-directory", results, "--minimum-expected-tests", minimum.ToString(System.Globalization.CultureInfo.InvariantCulture), .. filter],
                Repo.Root, timeout: TimeSpan.FromHours(3), echo: true);
            Log.Information("demos-ok lanes={Lanes}", string.Join(",", lanes.Select(static lane => lane.Name)));
        });
}
