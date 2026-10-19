# Qyl.Telemetry.AutoInstrumentation: build and release

## Facts

- Packages: every packable `src/` project ships to NuGet at one version: `Qyl.Telemetry.AutoInstrumentation`, `.DiagnosticListeners`, `.EntityFrameworkCore`, `.Hosting`, `.SqlClient` and `Qyl.Telemetry.Hosting`. `<IsPackable>false</IsPackable>` keeps a project out.
- Version: `<Version>` in `Directory.Build.props`. Its major is the generated-code ABI (`QylGeneratedCodeAbi.V<major>`). `Check` fails unless the top `CHANGELOG.md` entry is `## [<Version>] - YYYY-MM-DD`. nuget.org versions are immutable.
- Public API: `Check` rewrites `src/*/PublicAPI.txt` and reports `api: +N -M changed` or `api: unchanged`. A removed or changed line is a major bump, added lines only are minor.
- Semantic conventions: the `Qyl.Telemetry.SemanticConventions*` pins in `Directory.Packages.props`. `LiveCheck` judges against the registry tag `v` + that pin.
- NuGet key: `~/.config/vitals/nuget-qyl-manual-publish-key` (Vitals "qyl-dev manual publish and release (NuGet)"). `release.sh` reads it; never print it.

## Commands

- `make check` (`./eng/build.sh Check`, the CI gate): contracts, Release builds, the public API report, generator snapshots, package layout, consumer behavior including the NativeAOT consumer.
- `make check-full` (`./eng/build.sh CheckFull`): adds the NativeAOT publish matrix, the thirty demo lanes, `LiveCheck`, `Smoke`, `WebApiAotDemo` and `OtlpEvidence`.
- `make help`: every target with its parameters.
- `mise exec -- dotnet restore Qyl.Telemetry.AutoInstrumentation.slnx`: restore after a pin change. NU1102 for a version nuget.org lists is NuGet's HTTP cache: delete that id's `list_<id>.dat` under `dotnet nuget locals http-cache --list` and restore again.
- `./release.sh`: packs at `<Version>` (`./eng/build.sh Pack`, into `artifacts/release`), pushes to nuget.org, tags HEAD `v<Version>` and pushes the tag.
