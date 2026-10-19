using System.Text.RegularExpressions;

namespace Qyl.Build.Smoke;

/// <summary>
/// The package smoke test. A Qyl.Telemetry.Hosting package consumer and a ProjectReference consumer run
/// the same program managed and NativeAOT, each must print the verified stdout, each NativeAOT publish
/// must be free of trim, AOT and analyzer warnings, and a SqlClient-only package consumer must receive
/// the SQLCLIENT trace and metric generator manifest.
/// </summary>
static partial class SmokeGate
{
    private const string Fixture = "Smoke";

    public static void Run()
    {
        DotNet.Build(Repo.GeneratorProject);
        var version = LocalFeed.EnsurePacked();
        var verified = FixtureWorkspace.ReadFixture(Fixture, "verified/stdout.txt");

        var packageConsumer = FixtureWorkspace.Create("smoke-package-reference");
        FixtureWorkspace.CopyFixture(Fixture, packageConsumer, "Program.cs");
        ConsumerFixture.WriteProject(packageConsumer / "Consumer.csproj", "Microsoft.NET.Sdk",
            "    <OutputType>Exe</OutputType>\n" + ConsumerFixture.EmitGeneratedProperties +
            "    <DefineConstants>$(DefineConstants);QYL_SDK_PACKAGE_SMOKE</DefineConstants>\n",
            ConsumerFixture.PackageReference("Qyl.Telemetry.Hosting", version) +
            ConsumerFixture.PackageReference("Microsoft.Extensions.Hosting", Repo.Pin("Microsoft.Extensions.Hosting")) +
            ConsumerFixture.PackageReference("OpenTelemetry.Exporter.InMemory", Repo.Pin("OpenTelemetry.Exporter.InMemory")) +
            ConsumerFixture.RemoveGeneratedItem);
        RunConsumer("package-reference", packageConsumer, verified);

        var sqlClientConsumer = FixtureWorkspace.Create("smoke-sqlclient-package");
        File.Copy(Repo.Fixtures / Fixture / "SqlClient" / "Program.cs", sqlClientConsumer / "Program.cs");
        ConsumerFixture.WriteProject(sqlClientConsumer / "Consumer.csproj", "Microsoft.NET.Sdk",
            "    <OutputType>Exe</OutputType>\n" + ConsumerFixture.EmitGeneratedProperties,
            ConsumerFixture.PackageReference("Qyl.Telemetry.AutoInstrumentation.SqlClient", version) +
            ConsumerFixture.RemoveGeneratedItem);
        AssertSqlClientPackageAssets(sqlClientConsumer);

        var projectReferenceConsumer = FixtureWorkspace.Create("smoke-project-reference");
        FixtureWorkspace.CopyFixture(Fixture, projectReferenceConsumer, "Program.cs");
        ConsumerFixture.WriteProject(projectReferenceConsumer / "Consumer.csproj", "Microsoft.NET.Sdk",
            "    <OutputType>Exe</OutputType>\n" + ConsumerFixture.EmitGeneratedProperties +
            "    <DefineConstants>$(DefineConstants);QYL_PROJECTREFERENCE_SMOKE</DefineConstants>\n",
            $"    <ProjectReference Include=\"{Repo.AutoHostingProject}\" />\n" +
            $"    <ProjectReference Include=\"{Repo.GeneratorProject}\"\n" +
            "                      Condition=\"'$(PublishAot)' != 'true'\"\n" +
            "                      OutputItemType=\"Analyzer\"\n" +
            "                      ReferenceOutputAssembly=\"false\"\n" +
            "                      GlobalPropertiesToRemove=\"PublishAot;PublishSingleFile;PublishTrimmed;RuntimeIdentifier;RuntimeIdentifiers;SelfContained\" />\n" +
            $"    <Analyzer Include=\"{Repo.GeneratorAssembly}\" Condition=\"'$(PublishAot)' == 'true'\" />\n" +
            ConsumerFixture.RemoveGeneratedItem,
            Repo.CoreBuildTargets);
        RunConsumer("project-reference", projectReferenceConsumer, verified);

        Log.Information("smoketest-ok version={Version} rid={Rid}", version, Repo.RuntimeIdentifier);
    }

    private static void RunConsumer(string name, AbsolutePath directory, string verified)
    {
        var project = directory / "Consumer.csproj";

        // Managed: a forced, cache-free restore, a build that must emit the one interceptor source, and
        // the verified stdout.
        Shell.Checked("dotnet", ["restore", project, "--no-cache", "--force-evaluate", "-maxcpucount:4", "-v", "quiet"], directory);
        DotNet.Build(project, ["--no-restore"], workingDirectory: directory);
        AssertGeneratedInterceptor(directory);
        var managed = ConsumerFixture.RunAssembly(ConsumerFixture.Assembly(directory, "Consumer"));
        Gate.Require(managed.ExitCode == 0, () => $"{name} managed consumer failed\n" + managed.Describe());
        ConsumerFixture.RequireOutput($"{name} managed stdout", managed.Stdout, verified);

        // NativeAOT: the publish treats warnings as errors, and its log must not name a single trim, AOT
        // or analyzer diagnostic either.
        var rid = Repo.RuntimeIdentifier;
        Shell.Checked("dotnet", ["restore", project, "-r", rid, "-p:PublishAot=true", "--no-cache", "--force-evaluate", "-maxcpucount:4", "-v", "quiet"], directory);
        var publish = Shell.Run("dotnet",
        [
            "publish", project, "-c", "Release", "-r", rid, "-p:PublishAot=true", "-p:SelfContained=true",
            "-p:InvariantGlobalization=true", "-p:TreatWarningsAsErrors=true", "--no-restore", "-maxcpucount:4",
            "-nodeReuse:false", "-v", "quiet",
        ], directory);
        var log = publish.Stdout + publish.Stderr;
        File.WriteAllText(directory.Parent / $"{name}.nativeaot.publish.log", log);
        Gate.Require(publish.ExitCode == 0, () => $"{name} NativeAOT publish failed\n" + publish.Describe());
        var warnings = AotWarning().Matches(log).Select(static match => match.Value).Distinct().Order(StringComparer.Ordinal).ToArray();
        Gate.Require(warnings.Length == 0, () => $"AOT warning gate failed for {name}; found analyzer warnings:\n{string.Join("\n", warnings)}\n--- publish log ---\n{log}");
        Log.Information("aot-warning-gate-ok consumer={Consumer} warnings=0", name);

        var executable = directory / "bin" / "Release" / Repo.TargetFramework / rid / "publish" / Repo.ExecutableName("Consumer");
        var native = ConsumerFixture.RunExecutable(executable);
        Gate.Require(native.ExitCode == 0, () => $"{name} NativeAOT consumer failed\n" + native.Describe());
        ConsumerFixture.RequireOutput($"{name} NativeAOT stdout", native.Stdout, verified);
    }

    private static void AssertSqlClientPackageAssets(AbsolutePath directory)
    {
        var project = directory / "Consumer.csproj";
        Shell.Checked("dotnet", ["restore", project, "--no-cache", "--force-evaluate", "-maxcpucount:4", "-v", "quiet"], directory);
        DotNet.Build(project, ["--no-restore"], workingDirectory: directory);
        var generated = AssertGeneratedInterceptor(directory);
        var text = File.ReadAllText(generated);
        Gate.Require(text.Contains("signals.traces.SQLCLIENT", StringComparison.Ordinal) && text.Contains("signals.metrics.SQLCLIENT", StringComparison.Ordinal),
            "SqlClient-only package consumer did not receive the SQLCLIENT trace+metric generator manifest");
        Log.Information("sqlclient-package-assets-ok");
    }

    private static AbsolutePath AssertGeneratedInterceptor(AbsolutePath directory)
    {
        var files = ConsumerFixture.GeneratedInterceptors(directory);
        Gate.Require(files.Length == 1, () =>
            $"expected one generated interceptor source in {directory}, found {files.Length}\n" +
            string.Join("\n", (directory / "Generated").DirectoryExists() ? (directory / "Generated").GlobFiles("**/*").Select(static file => file.ToString()) : []));
        return files[0];
    }

    [GeneratedRegex(@"\b(IL2[0-9]{3}|IL3[0-9]{3}|IL4[0-9]{3}|CA[0-9]{4})\b")]
    private static partial Regex AotWarning();
}
