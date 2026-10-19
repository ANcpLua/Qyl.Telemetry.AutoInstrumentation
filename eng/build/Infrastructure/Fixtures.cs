using System.Text;

namespace Qyl.Build.Infrastructure;

/// <summary>
/// The packages this checkout would ship, packed once per build into artifacts/fixtures/feed. A
/// fixture consumer restores them from that folder alone: the same id and version can already be on
/// nuget.org with other content, so package source mapping pins the qyl ids to the local feed.
/// </summary>
static class LocalFeed
{
    private static bool _packed;

    public static AbsolutePath Directory => FixtureWorkspace.Root / "feed";

    /// <summary>Packs every packable src project at the Directory.Build.props version; returns that version.</summary>
    public static string EnsurePacked()
    {
        var version = Repo.Version;
        if (_packed)
            return version;

        Directory.CreateOrCleanDirectory();
        foreach (var project in Repo.PackableProjects)
            DotNet.Pack(project, Directory);
        _packed = true;
        Log.Information("local feed: {Count} packages at {Version} in {Feed}", Repo.PackableProjects.Count, version, Repo.Relative(Directory));
        return version;
    }
}

/// <summary>
/// Isolated consumer projects under artifacts/fixtures. The repository's Directory.Build.props,
/// Directory.Packages.props and nuget.config stop above this folder, so a consumer sees exactly what a
/// NuGet consumer sees; its packages restore into a folder of their own, never into the machine's
/// global cache, where a nuget.org copy of the same version may already sit.
/// </summary>
static class FixtureWorkspace
{
    private static bool _prepared;

    public static AbsolutePath Root => Repo.Artifacts / "fixtures";

    /// <summary>The packages this checkout ships, mapped to the local feed.</summary>
    private static readonly string[] LocalPackagePatterns =
    [
        "Qyl.Telemetry.AutoInstrumentation",
        "Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners",
        "Qyl.Telemetry.AutoInstrumentation.EntityFrameworkCore",
        "Qyl.Telemetry.AutoInstrumentation.Hosting",
        "Qyl.Telemetry.AutoInstrumentation.SqlClient",
        "Qyl.Telemetry.Hosting",
    ];

    /// <summary>A clean directory for one consumer.</summary>
    public static AbsolutePath Create(string name)
    {
        Prepare();
        var directory = Root / "work" / name;
        directory.CreateOrCleanDirectory();
        return directory;
    }

    /// <summary>Copies a checked-in fixture from eng/fixtures/<paramref name="fixture"/> into <paramref name="destination"/>.</summary>
    public static void CopyFixture(string fixture, AbsolutePath destination, params string[] files)
    {
        var source = Repo.Fixtures / fixture;
        Gate.Require(source.DirectoryExists(), $"missing fixture {Repo.Relative(source)}");
        foreach (var file in files)
        {
            var from = source / file;
            Gate.Require(from.FileExists(), $"missing fixture file {Repo.Relative(from)}");
            (destination / file).Parent.CreateDirectory();
            File.Copy(from, destination / file, overwrite: true);
        }
    }

    public static string ReadFixture(string fixture, string file)
    {
        var path = Repo.Fixtures / fixture / file;
        Gate.Require(path.FileExists(), $"missing fixture file {Repo.Relative(path)}");
        return File.ReadAllText(path);
    }

    private static void Prepare()
    {
        if (_prepared)
            return;
        _prepared = true;

        Root.CreateDirectory();
        File.WriteAllText(Root / "Directory.Build.props", "<Project />\n");
        File.WriteAllText(Root / "Directory.Build.targets", "<Project />\n");
        File.WriteAllText(Root / "Directory.Packages.props", "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n");

        var packages = Root / "packages";
        packages.CreateDirectory();
        // The qyl ids are rebuilt by every run under one version number; only third-party packages
        // may be reused from an earlier run.
        foreach (var pattern in LocalPackagePatterns)
            (packages / pattern.ToLowerInvariant()).DeleteDirectory();

        var config = new StringBuilder()
            .AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
            .AppendLine("<configuration>")
            .AppendLine("  <config>")
            .AppendLine($"    <add key=\"globalPackagesFolder\" value=\"{packages}\" />")
            .AppendLine("  </config>")
            .AppendLine("  <packageSources>")
            .AppendLine("    <clear />")
            .AppendLine($"    <add key=\"qyl-local\" value=\"{LocalFeed.Directory}\" />")
            .AppendLine($"    <add key=\"nuget.org\" value=\"{Repo.NuGetOrg}\" />")
            .AppendLine("  </packageSources>")
            .AppendLine("  <packageSourceMapping>")
            .AppendLine("    <packageSource key=\"qyl-local\">");
        foreach (var pattern in LocalPackagePatterns)
            config.AppendLine($"      <package pattern=\"{pattern}\" />");
        config.AppendLine("    </packageSource>")
            .AppendLine("    <packageSource key=\"nuget.org\">")
            .AppendLine("      <package pattern=\"*\" />")
            .AppendLine("    </packageSource>")
            .AppendLine("  </packageSourceMapping>")
            .AppendLine("</configuration>");
        File.WriteAllText(Root / "nuget.config", config.ToString());
    }
}
