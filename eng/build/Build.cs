
namespace Qyl.Build;

/// <summary>
/// Every build, pack and gate of this repository. <c>./eng/build.sh --help</c> lists the targets;
/// CI runs <c>Check</c>, <c>CheckFull</c> runs everything.
/// </summary>
sealed partial class QylBuild : FalloutBuild
{
    public static int Main() => Execute<QylBuild>(static build => build.Build);

    /// <summary>After a green run drops artifacts/publish: the NativeAOT
    /// binaries are a pure verification byproduct (several GB over the full matrix), executed and asserted
    /// within the run. A failed run keeps them for inspection.</summary>
    protected override void OnBuildFinished()
    {
        var publish = Repo.Artifacts / "publish";
        if (IsSucceeding && publish.DirectoryExists())
        {
            try
            {
                publish.DeleteDirectory();
                Log.Information("removed {Publish}", Repo.Relative(publish));
            }
            catch (IOException exception)
            {
                Log.Warning("{Publish} not removed: {Message}", Repo.Relative(publish), exception.Message);
            }
        }

        base.OnBuildFinished();
    }

    Target Build => definition => definition
        .Description("Release build of the solution and of the demo solution")
        .After(Contracts)
        .Executes(() =>
        {
            DotNet.Build(Repo.Solution);
            DotNet.Build(Repo.DemoSolution);
            Log.Information("release build ok: {Solution}, {Demos}", Repo.Solution.Name, Repo.DemoSolution.Name);
        });

    [JetBrains.Annotations.UsedImplicitly]
    Target Pack => definition => definition
        .Description("Pack every packable project under src/ at the Directory.Build.props version into artifacts/release")
        .Executes(ReleasePack.Run);

    Target Check => definition => definition
        .Description("Contracts, release builds, the public API report, generator snapshots, package layout and consumer behavior (CI runs this)")
        .DependsOn(Contracts, Build, ApiBaseline, GeneratorSnapshots, PackageLayout, ConsumerBehavior)
        .Executes(() =>
        {
            Shell.Checked("git", ["diff", "--check"], Repo.Root);
            Log.Information("check-ok");
        });

    [JetBrains.Annotations.UsedImplicitly]
    Target CheckFull => definition => definition
        .Description("Everything: Check, the NativeAOT publish matrix, all thirty demos, the live check, smoke, web API AOT demo and OTLP wire evidence")
        .DependsOn(Check, AotPublishGate, Demos, LiveCheck, Smoke, WebApiAotDemo, OtlpEvidence)
        .Executes(() => Log.Information("check-full-ok"));
}

/// <summary>The release pack: the derived packable set, one version, one output folder.</summary>
static class ReleasePack
{
    private static AbsolutePath Output => Repo.Artifacts / "release";

    public static void Run()
    {
        var version = Repo.Version;
        var projects = Repo.PackableProjects;
        Gate.Require(projects.Count > 0, "no packable project under src/");
        Output.CreateOrCleanDirectory();
        foreach (var project in projects)
            DotNet.Pack(project, Output, ["-p:Version=" + version]);

        var packages = Output.GlobFiles("*.nupkg").Select(static package => package.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        var expected = projects.Select(project => $"{project.NameWithoutExtension}.{version}.nupkg").OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        Gate.Require(packages.SequenceEqual(expected), () => $"packed {string.Join(", ", packages)}; expected {string.Join(", ", expected)}");
        foreach (var package in packages)
            Log.Information("packed {Package}", package);
        Log.Information("pack-ok version={Version} packages={Count} output={Output}", version, packages.Length, Repo.Relative(Output));
    }
}
