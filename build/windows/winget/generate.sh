#!/usr/bin/env bash
#
# Emits the three manifests into dist/winget/manifests/p/Polemus/Omnigit/<version>/
#
# Usage: build/windows/winget/generate.sh [version]
#
# The path is generated from the version the files declare: validation checks
# that directory and file names match PackageIdentifier and PackageVersion
# exactly, case included.
#
# The checksum comes from the published release, not dist/ - winget downloads
# the URL and compares, so a local hash fails there rather than here.
#
# Requires: curl, sha256sum.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
. "$ROOT/build/version.sh"

VERSION="${1:-$(project_version "$ROOT")}"
HERE="$ROOT/build/windows/winget"
ID=Polemus.Omnigit
REPO="https://github.com/Polemus/Omnigit"
TAG="v$VERSION"

# manifests/<first letter of publisher, lowercased>/<Publisher>/<Package>/<version>/
OUT="$ROOT/dist/winget/manifests/p/Polemus/Omnigit/$VERSION"

INSTALLER_URL="$REPO/releases/download/$TAG/Omnigit-$VERSION-win-x64-setup.exe"

echo "==> $ID $VERSION from $TAG"

echo "--> hashing the Windows installer"
if ! SHA="$(set -o pipefail; curl -fsSL "$INSTALLER_URL" | sha256sum | cut -d' ' -f1)"; then
    echo "!! could not fetch $INSTALLER_URL" >&2
    echo "   is the release published, and did the installer upload?" >&2
    exit 1
fi
# Every manifest in the repository is uppercase.
SHA="$(echo "$SHA" | tr 'a-f' 'A-F')"

# The release's own date, so regenerating an old tag later stays accurate.
echo "--> reading the release date"
RELEASE_DATE="$(curl -fsSL "https://api.github.com/repos/Polemus/Omnigit/releases/tags/$TAG" \
    | sed -n 's/.*"published_at": *"\([0-9-]*\)T.*/\1/p' | head -n1)"

if [ -z "$RELEASE_DATE" ]; then
    echo "!! could not read published_at for $TAG" >&2
    exit 1
fi

mkdir -p "$OUT"
for part in "$ID.yaml" "$ID.locale.en-US.yaml" "$ID.installer.yaml"; do
    sed \
        -e "s|@VERSION@|$VERSION|g" \
        -e "s|@SHA256_INSTALLER@|$SHA|g" \
        -e "s|@RELEASE_DATE@|$RELEASE_DATE|g" \
        "$HERE/$part.in" > "$OUT/$part"

    if grep -q '@[A-Z0-9_]*@' "$OUT/$part"; then
        echo "!! a placeholder was left unfilled in $part" >&2
        grep -n '@[A-Z0-9_]*@' "$OUT/$part" >&2
        exit 1
    fi
done

echo "==> Wrote:"
ls -la "$OUT"
echo
echo "    installer  $SHA"
echo "    released   $RELEASE_DATE"
echo
echo "Validate on a Windows machine with:"
echo "    winget validate --manifest $OUT"
