#!/usr/bin/env bash
# ./release.sh: packs every packable src/ project at Directory.Build.props <Version> (./eng/build.sh Pack),
# pushes the packages to nuget.org, tags HEAD v<Version> and pushes the tag. A rerun skips what is
# already pushed or tagged.
set -euo pipefail
cd "$(dirname "$0")"

key_file=$HOME/.config/vitals/nuget-qyl-manual-publish-key  # vitals keys: "qyl-dev manual publish and release (NuGet)"
die() { echo "release: $*" >&2; exit 1; }

version=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' Directory.Build.props | head -1)
[[ -n $version ]] || die "Directory.Build.props declares no <Version>"
[[ -z $(git status --porcelain --untracked-files=no) ]] || die "uncommitted changes: v$version must tag what is packed"
if tagged=$(git rev-parse -q --verify "refs/tags/v$version^{commit}"); then
  [[ $tagged == $(git rev-parse HEAD) ]] || die "v$version already tags $tagged; bump <Version> in Directory.Build.props"
fi
key=$(<"$key_file")

./eng/build.sh Pack
for package in artifacts/release/*.nupkg; do
  NUGET_API_KEY=$key dotnet nuget push "$package" --source https://api.nuget.org/v3/index.json --skip-duplicate
done

git rev-parse -q --verify "refs/tags/v$version" >/dev/null || git tag -a "v$version" -m "v$version"
git push origin "v$version"
