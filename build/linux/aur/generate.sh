#!/usr/bin/env bash
#
# Emits dist/aur/PKGBUILD.
#
# Usage: build/linux/aur/generate.sh [version]
#
# Checksums come from the published release, not from dist/. A PKGBUILD points
# users at a URL, so hashing a local build would produce one that works here and
# fails everywhere else.
#
# Requires: curl, sha256sum.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
. "$ROOT/build/version.sh"

VERSION="${1:-$(project_version "$ROOT")}"
HERE="$ROOT/build/linux/aur"
OUT="$ROOT/dist/aur"
REPO="https://github.com/Polemus/Omnigit"
TAG="v$VERSION"

# A pkgver cannot contain a hyphen; makepkg separates pkgver from pkgrel with it.
case "$VERSION" in
    *-*)
        echo "!! $VERSION contains a hyphen, which a pkgver may not" >&2
        echo "   AUR versions use 1.0.0rc1 rather than 1.0.0-rc1" >&2
        exit 1
        ;;
esac

# --fail, or a 404 page gets hashed. pipefail inside the subshell carries curl's
# status out; without it the status is sha256sum's, which is always 0.
sha256_of() {  # sha256_of <url> <what>
    local sum
    echo "--> hashing $2" >&2
    if ! sum="$(set -o pipefail; curl -fsSL "$1" | sha256sum | cut -d' ' -f1)"; then
        echo "!! could not fetch $1" >&2
        echo "   is the release published, and did every artifact upload?" >&2
        exit 1
    fi
    echo "$sum"
}

echo "==> omnigit-bin $VERSION from $TAG"

META_URL="$REPO/archive/refs/tags/$TAG.tar.gz"
X86_URL="$REPO/releases/download/$TAG/Omnigit-$VERSION-linux-x64.tar.gz"
ARM_URL="$REPO/releases/download/$TAG/Omnigit-$VERSION-linux-arm64.tar.gz"

SHA_META="$(sha256_of "$META_URL" 'the tagged source archive')"
SHA_X86="$(sha256_of "$X86_URL" 'the x86_64 release tarball')"
SHA_ARM="$(sha256_of "$ARM_URL" 'the aarch64 release tarball')"

mkdir -p "$OUT"
sed \
    -e "s|@VERSION@|$VERSION|g" \
    -e "s|@SHA256_META@|$SHA_META|g" \
    -e "s|@SHA256_X86_64@|$SHA_X86|g" \
    -e "s|@SHA256_AARCH64@|$SHA_ARM|g" \
    "$HERE/PKGBUILD.in" > "$OUT/PKGBUILD"

if grep -q '@[A-Z0-9_]*@' "$OUT/PKGBUILD"; then
    echo "!! a placeholder was left unfilled - PKGBUILD.in gained one this script does not know" >&2
    grep -n '@[A-Z0-9_]*@' "$OUT/PKGBUILD" >&2
    exit 1
fi

echo "==> Wrote $OUT/PKGBUILD"
echo
echo "    source   $SHA_META"
echo "    x86_64   $SHA_X86"
echo "    aarch64  $SHA_ARM"
echo
echo "No .SRCINFO here: generating one needs makepkg, which needs Arch."
echo ".github/workflows/aur.yml makes it in an archlinux container."
