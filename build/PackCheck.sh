#!/bin/sh

set -e

# Packs the real .nupkg and runs the package tests against it, without needing Windows.
#
#   ./build/PackCheck.sh
#
# build/CompileCheck.sh answers "does the C# compile". This answers the question after it: "is the
# package a consumer restores actually right" — which is a different failure surface, and the one
# that has cost the most CI round trips. One real bug it would have caught locally:
#
#   NU5030   the licence file 'LICENSE' does not exist in the package — from a too-broad
#            IncludeContentInPack=false, which despite its name gates *all* explicitly packed files.
#
# THE HACK, stated plainly: packing runs MakePri.exe, which is a Windows binary. The target that
# invokes it declares Outputs="$(ProjectPriFullPath)", so pre-creating an empty file at that path
# makes MSBuild skip the target as up to date. Its inputs are regenerated whenever the project file
# changes, which can leave them newer than the placeholder — so the first run after an edit may
# still reach MakePri.exe and fail with "Exec format error". Run it again.
#
# The consequence is that the .pri files in a package produced here are empty placeholders. That is
# fine for what this script checks — which files are present, and which are not — and useless for
# anything about resource indexing. Which is also its blind spot, and the one that cost four round
# trips: the four MSB3030s a consumer hit were *names inside the .pri*, put there by MakePri
# indexing OpenTok.Client's native payload as this library's @(Content). A package produced here
# cannot show that, because MakePri never ran. Only the Windows pack job can, and
# PackageTests.Does_not_name_the_native_payload_in_its_resource_index is what reads it there.
#
# It is a development aid, not a substitute for the Windows pack job, and nothing it produces
# should ever be published.

cd "$(dirname "$0")/.."

PROJECT="src/OpenTok.Net.Win/OpenTok.Net.Win.csproj"
OUTPUT="${TMPDIR:-/tmp}/opentok-net-win-packcheck"

FRAMEWORKS="$(dotnet msbuild "$PROJECT" -getProperty:TargetFrameworks -p:EnableWindowsTargeting=true \
    | tr -d '\r\n' | tr ';' ' ')"

if [ -z "$FRAMEWORKS" ]; then
    echo "error: could not read TargetFrameworks from $PROJECT" >&2
    exit 1
fi

for tfm in $FRAMEWORKS; do
    dir="src/OpenTok.Net.Win/bin/Release/$tfm"
    mkdir -p "$dir"
    : > "$dir/OpenTok.Net.Win.pri"
done

rm -rf "$OUTPUT"

echo "==> packing"
dotnet pack "$PROJECT" --configuration Release -p:EnableWindowsTargeting=true -o "$OUTPUT" >/dev/null

echo "==> package tests"
OPENTOK_ARTIFACTS="$OUTPUT" dotnet test tests/OpenTok.Net.Win.PackageTests --nologo

# Removed rather than left behind: a stale empty .pri would make a later real build on Windows skip
# PRI generation and ship the placeholder.
for tfm in $FRAMEWORKS; do
    rm -f "src/OpenTok.Net.Win/bin/Release/$tfm/OpenTok.Net.Win.pri"
done

echo "==> package looks right ($OUTPUT)"
