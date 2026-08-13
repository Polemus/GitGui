#!/usr/bin/env bash
#
# Regenerates the offline NuGet package lists the Flatpak build reads:
#   build/linux/flatpak/nuget-sources-x86_64.json
#   build/linux/flatpak/nuget-sources-aarch64.json
#
# Usage: build/linux/flatpak/generate-nuget-sources.sh
#
# A Flatpak build has no network, so every .nupkg has to be named, pinned by
# hash and fetched by flatpak-builder before the build starts. That is what
# these two files are: a plain list of URLs and SHA-512s produced by restoring
# the project once per target architecture.
#
# RUN THIS WHENEVER A PackageReference IN GitGui.csproj CHANGES - a version bump
# from Dependabot counts. Nothing else notices: the app builds fine everywhere
# else, and the Flatpak build fails with a restore error naming a package it was
# never given. The check in .github/workflows/ci.yml is there to catch the case
# where somebody forgets.
#
# Requires: flatpak, python3, and the runtimes the manifest builds against.
# Unlike everything else in build/, this step needs network - it is doing the
# downloading that the real build is not allowed to do.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
HERE="$ROOT/build/linux/flatpak"
TOOLS="$ROOT/build/.tools"
PROJECT="src/GitGui/GitGui.csproj"

# Kept in step with the manifest by reading them out of it, so bumping the
# runtime is a one-line edit there rather than a two-place one.
MANIFEST="$HERE/io.github.polemus.GitGui.yml"
FREEDESKTOP="$(sed -n "s/^runtime-version: *'\{0,1\}\([0-9.]*\)'\{0,1\}.*/\1/p" "$MANIFEST")"
DOTNET="$(sed -n 's/.*org\.freedesktop\.Sdk\.Extension\.dotnet\([0-9]*\).*/\1/p' "$MANIFEST" | head -n1)"

: "${FREEDESKTOP:?could not read runtime-version out of $MANIFEST}"
: "${DOTNET:?could not read the dotnet SDK extension out of $MANIFEST}"

echo "==> freedesktop $FREEDESKTOP, dotnet $DOTNET"

for tool in flatpak python3; do
    command -v "$tool" >/dev/null 2>&1 || { echo "!! $tool is required" >&2 ; exit 1 ; }
done

for ref in "org.freedesktop.Sdk//$FREEDESKTOP" "org.freedesktop.Sdk.Extension.dotnet$DOTNET//$FREEDESKTOP"; do
    if ! flatpak info "$ref" >/dev/null 2>&1; then
        echo "!! $ref is not installed. Install it with:" >&2
        echo "     flatpak install flathub $ref" >&2
        exit 1
    fi
done

# The generator is a single MIT-licensed script from flatpak-builder-tools.
# Cached next to appimagetool rather than vendored, so it doesn't become a copy
# of somebody else's file that nobody remembers to update.
GENERATOR="$TOOLS/flatpak-dotnet-generator.py"
if [ ! -s "$GENERATOR" ]; then
    echo "==> Fetching flatpak-dotnet-generator.py"
    mkdir -p "$TOOLS"
    curl -fsSL --retry 3 -o "$GENERATOR.part" \
        https://raw.githubusercontent.com/flatpak/flatpak-builder-tools/master/dotnet/flatpak-dotnet-generator.py
    mv "$GENERATOR.part" "$GENERATOR"
fi

# The generator restores into a scratch directory it makes inside the *working*
# directory, and the restore itself happens inside a Flatpak sandbox. That rules
# out /tmp: the sandbox has its own, so a restore run from there succeeds and
# then leaves nothing behind to read. Somewhere under the repository is a path
# both sides agree on.
WORK="$ROOT/build/.nuget-restore"
rm -rf "$WORK"
mkdir -p "$WORK"
trap 'rm -rf "$WORK"' EXIT

# One restore per architecture, tagged with only-arches so an aarch64 builder
# never downloads the x86_64 runtime pack or the other way round.
generate() {  # generate <flatpak-arch> <runtime-identifier>
    local arch="$1" rid="$2"
    echo "==> Restoring for $rid"
    ( cd "$WORK" && python3 "$GENERATOR" \
        "$HERE/nuget-sources-$arch.json" \
        "$ROOT/$PROJECT" \
        --runtime "$rid" \
        --dotnet "$DOTNET" \
        --freedesktop "$FREEDESKTOP" \
        --only-arches "$arch" )

    local count
    count="$(python3 -c 'import json,sys; print(len(json.load(open(sys.argv[1]))))' \
        "$HERE/nuget-sources-$arch.json")"

    # A restore that fails still leaves the generator writing an empty list, and
    # it exits 0 either way - check_output is off in the script it runs.
    if [ "$count" -eq 0 ]; then
        echo "!! restore for $rid produced no packages - look at the output above" >&2
        exit 1
    fi
    echo "==> $arch: $count packages"
}

generate x86_64 linux-x64
generate aarch64 linux-arm64

echo "==> Done. Commit the regenerated files:"
ls -la "$HERE"/nuget-sources-*.json
