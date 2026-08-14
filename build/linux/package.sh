#!/usr/bin/env bash
#
# Builds Linux artifacts for one runtime identifier:
#   dist/Omnigit-<version>-<rid>.tar.gz     portable tarball
#   dist/omnigit_<version>_<arch>.deb       Debian / Ubuntu / Mint
#   dist/omnigit-<version>-1.<arch>.rpm     Fedora / RHEL / openSUSE
#   dist/Omnigit-<version>-<arch>.AppImage  everything else
#
# Usage: build/linux/package.sh <rid> [version]
#   rid: linux-x64 | linux-arm64
#   version: defaults to <Version> in the csproj - see build/version.sh
#
# Requires: dotnet SDK, tar, and fpm (gem install fpm) for deb/rpm. The
# AppImage additionally needs curl, and is skipped without it.
set -euo pipefail

RID="${1:?usage: package.sh <linux-x64|linux-arm64> [version]}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
. "$ROOT/build/version.sh"
VERSION="${2:-$(project_version "$ROOT")}"
DIST="$ROOT/dist"
STAGE="$ROOT/build/.stage-$RID"

case "$RID" in
    linux-x64)   DEB_ARCH=amd64 ; RPM_ARCH=x86_64  ;;
    linux-arm64) DEB_ARCH=arm64 ; RPM_ARCH=aarch64 ;;
    *) echo "unsupported rid: $RID" >&2 ; exit 1 ;;
esac

echo "==> Publishing $RID (self-contained, $VERSION)"
rm -rf "$STAGE"
mkdir -p "$DIST" "$STAGE/usr/lib/omnigit" "$STAGE/usr/bin" "$STAGE/usr/share/applications"

dotnet publish "$ROOT/src/Omnigit/Omnigit.csproj" \
    --configuration Release \
    --runtime "$RID" \
    --self-contained true \
    -p:Version="$VERSION" \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    --output "$STAGE/usr/lib/omnigit"

chmod +x "$STAGE/usr/lib/omnigit/Omnigit"

# /usr/bin/omnigit -> the real binary next to its runtime files
ln -sf ../lib/omnigit/Omnigit "$STAGE/usr/bin/omnigit"

echo "==> Laying out desktop integration"

# Everything a desktop environment reads is named after the app id, not the
# binary: that is what Flatpak requires and what lets one desktop entry and one
# metainfo file serve all four package formats. The command stays "omnigit".
APP_ID=io.github.polemus.Omnigit

install -Dm644 "$ROOT/build/linux/$APP_ID.desktop" \
    "$STAGE/usr/share/applications/$APP_ID.desktop"

# Without this a software centre lists Omnigit as a bare package name and no
# description. It is the same file the Flatpak ships.
install -Dm644 "$ROOT/build/linux/$APP_ID.metainfo.xml" \
    "$STAGE/usr/share/metainfo/$APP_ID.metainfo.xml"

for size in 16 32 48 64 128 256; do
    install -Dm644 "$ROOT/build/linux/icons/omnigit-${size}.png" \
        "$STAGE/usr/share/icons/hicolor/${size}x${size}/apps/$APP_ID.png"
done
install -Dm644 "$ROOT/src/Omnigit/Assets/omnigit.svg" \
    "$STAGE/usr/share/icons/hicolor/scalable/apps/$APP_ID.svg"

echo "==> Portable tarball"
tar -czf "$DIST/Omnigit-$VERSION-$RID.tar.gz" -C "$STAGE/usr/lib" omnigit

# Before the fpm check below, which exits when fpm is absent.
"$ROOT/build/linux/appimage.sh" "$RID" "$VERSION" "$STAGE"

if ! command -v fpm >/dev/null 2>&1; then
    echo "!! fpm not found - skipping .deb/.rpm (install with: gem install fpm)" >&2
    exit 0
fi

COMMON_ARGS=(
    -s dir
    -C "$STAGE"
    --name omnigit
    --version "$VERSION"
    --license MIT
    --vendor Polemus
    --maintainer "Polemus <112549+Polemus@users.noreply.github.com>"
    --url "https://github.com/Polemus/Omnigit"
    --description "Desktop git client for GitHub, Gitea and other forges."
    --force
)

echo "==> Building .deb"
fpm "${COMMON_ARGS[@]}" \
    -t deb \
    --architecture "$DEB_ARCH" \
    --depends libx11-6 \
    --depends libice6 \
    --depends libsm6 \
    --depends libfontconfig1 \
    --package "$DIST/omnigit_${VERSION}_${DEB_ARCH}.deb" \
    usr

echo "==> Building .rpm"
fpm "${COMMON_ARGS[@]}" \
    -t rpm \
    --architecture "$RPM_ARCH" \
    --depends libX11 \
    --depends libICE \
    --depends libSM \
    --depends fontconfig \
    --package "$DIST/omnigit-${VERSION}-1.${RPM_ARCH}.rpm" \
    usr

echo "==> Done:"
ls -la "$DIST"
