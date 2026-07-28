#!/bin/sh

set -e

# Compiles every Windows target framework without needing Windows.
#
#   ./build/CompileCheck.sh
#
# Why this exists: everything in this repository except the converter tests targets
# -windows10.0.19041.0, and the obvious conclusion — that none of it can be checked off Windows —
# is wrong in a way that costs a CI round trip per typo. Two flags make it work:
#
#   EnableWindowsTargeting=true   restores the Windows reference packs on a non-Windows host.
#                                 Without it the SDK refuses with NETSDK1100.
#   -t:Compile                    stops after the C# compiler. A full build goes on to run
#                                 MakePri.exe, a native Windows tool that fails with "Exec format
#                                 error" here — packaging genuinely does need Windows, compiling
#                                 does not.
#
# So this catches everything the compiler can catch: missing members, wrong signatures, the
# interface-constraint mismatches that the SDK's XML documentation does not record. It cannot catch
# anything about packing, MSIX, or running — build/AddWindowsAssets.sh in the OpenTok.Net repository
# and CI still own those.

cd "$(dirname "$0")/.."

PROJECT="src/OpenTok.Net.Win/OpenTok.Net.Win.csproj"

# Read the target framework list from the project rather than repeating it, so a target framework
# added to Directory.Build.props is checked here without anyone remembering to update this script.
FRAMEWORKS="$(dotnet msbuild "$PROJECT" -getProperty:TargetFrameworks -p:EnableWindowsTargeting=true \
    | tr -d '\r\n' | tr ';' ' ')"

if [ -z "$FRAMEWORKS" ]; then
    echo "error: could not read TargetFrameworks from $PROJECT" >&2
    exit 1
fi

failed=0

for tfm in $FRAMEWORKS; do
    printf '==> %s ' "$tfm"

    if output="$(dotnet build "$PROJECT" \
        --configuration Release \
        --framework "$tfm" \
        -p:EnableWindowsTargeting=true \
        -t:Compile 2>&1)"; then
        echo "ok"
    else
        echo "FAILED"
        echo "$output" | grep -E 'error [A-Z]+[0-9]+' | sort -u
        failed=1
    fi
done

if [ "$failed" -ne 0 ]; then
    echo "==> compile check failed" >&2
    exit 1
fi

echo "==> every Windows target framework compiles"
