#!/usr/bin/env bash
# Builds and runs the Fallout build project under eng/build. Every gate, build and pack of this
# repository is a target there: ./eng/build.sh --help lists them, ./eng/build.sh <Target> runs one.
set -eo pipefail
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
BUILD_PROJECT_FILE="$SCRIPT_DIR/build/build.csproj"

# Quiet unless --verbose or QYL_VERBOSE=1: eng/quiet.sh prints the target table, PASS or FAIL and
# the failing target's last lines, and keeps the whole output in artifacts/logs/build-<targets>.log.
verbose=${QYL_VERBOSE:-0}
arguments=()
name=build
for argument in "$@"; do
    case $argument in
        --verbose) verbose=1; continue ;;
        --help | -h) verbose=1 ;;
        -*) parameters=1 ;;
        *) [[ -n ${parameters:-} ]] || name+="-$argument" ;;
    esac
    arguments+=("$argument")
done
if [[ $verbose != 1 ]]; then
    exec "$SCRIPT_DIR/quiet.sh" "${name//[^A-Za-z0-9._-]/_}" -- "$SCRIPT_DIR/build.sh" ${arguments[@]+"${arguments[@]}"}
fi
set -- ${arguments[@]+"${arguments[@]}"}

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
# This machine shares its cores with other gates: every build here runs on at most four.
export DOTNET_PROCESSOR_COUNT=4
export MSBUILDDISABLENODEREUSE=1

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet is not on PATH; run 'mise install' (mise.toml pins the SDK global.json names)" >&2
    exit 1
fi

git -C "$SCRIPT_DIR/.." submodule update --init --checkout -- eng/dependencies/Qyl.Telemetry.Tests

dotnet build "$BUILD_PROJECT_FILE" -maxcpucount:4 -nodeReuse:false -p:UseSharedCompilation=false -nologo -clp:NoSummary --verbosity quiet
dotnet run --project "$BUILD_PROJECT_FILE" --no-build -- "$@"
