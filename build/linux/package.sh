#!/usr/bin/env bash
#
# Builds Linux artifacts for one runtime identifier:
#   dist/GitGui-<version>-<rid>.tar.gz     portable tarball
#   dist/gitgui_<version>_<arch>.deb       Debian / Ubuntu / Mint
#   dist/gitgui-<version>-1.<arch>.rpm     Fedora / RHEL / openSUSE
#   dist/GitGui-<version>-<arch>.AppImage  everything else
#
# Usage: build/linux/package.sh <rid> [version]
#   rid: linux-x64 | linux-arm64
#
# Requires: dotnet SDK, tar, and fpm (gem install fpm) for deb/rpm. The
# AppImage additionally needs curl, and is skipped without it.
set -euo pipefail

RID="${1:?usage: package.sh <linux-x64|linux-arm64> [version]}"
VERSION="${2:-0.1.0}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DIST="$ROOT/dist"
STAGE="$ROOT/build/.stage-$RID"

case "$RID" in
    linux-x64)   DEB_ARCH=amd64 ; RPM_ARCH=x86_64  ;;
    linux-arm64) DEB_ARCH=arm64 ; RPM_ARCH=aarch64 ;;
    *) echo "unsupported rid: $RID" >&2 ; exit 1 ;;
esac

echo "==> Publishing $RID (self-contained, $VERSION)"
rm -rf "$STAGE"
mkdir -p "$DIST" "$STAGE/usr/lib/gitgui" "$STAGE/usr/bin" "$STAGE/usr/share/applications"

dotnet publish "$ROOT/src/GitGui/GitGui.csproj" \
    --configuration Release \
    --runtime "$RID" \
    --self-contained true \
    -p:Version="$VERSION" \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    --output "$STAGE/usr/lib/gitgui"

chmod +x "$STAGE/usr/lib/gitgui/GitGui"

# /usr/bin/gitgui -> the real binary next to its runtime files
ln -sf ../lib/gitgui/GitGui "$STAGE/usr/bin/gitgui"

echo "==> Laying out desktop integration"
install -Dm644 "$ROOT/build/linux/gitgui.desktop" \
    "$STAGE/usr/share/applications/gitgui.desktop"

for size in 16 32 48 64 128 256; do
    install -Dm644 "$ROOT/build/linux/icons/gitgui-${size}.png" \
        "$STAGE/usr/share/icons/hicolor/${size}x${size}/apps/gitgui.png"
done
install -Dm644 "$ROOT/src/GitGui/Assets/gitgui.svg" \
    "$STAGE/usr/share/icons/hicolor/scalable/apps/gitgui.svg"

echo "==> Portable tarball"
tar -czf "$DIST/GitGui-$VERSION-$RID.tar.gz" -C "$STAGE/usr/lib" gitgui

# Before the fpm check below, which exits when fpm is absent.
"$ROOT/build/linux/appimage.sh" "$RID" "$VERSION" "$STAGE"

if ! command -v fpm >/dev/null 2>&1; then
    echo "!! fpm not found - skipping .deb/.rpm (install with: gem install fpm)" >&2
    exit 0
fi

COMMON_ARGS=(
    -s dir
    -C "$STAGE"
    --name gitgui
    --version "$VERSION"
    --license MIT
    --vendor Polemus
    --maintainer "Polemus <dusty.roberts101@gmail.com>"
    --url "https://github.com/Polemus/GitGui"
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
    --package "$DIST/gitgui_${VERSION}_${DEB_ARCH}.deb" \
    usr

echo "==> Building .rpm"
fpm "${COMMON_ARGS[@]}" \
    -t rpm \
    --architecture "$RPM_ARCH" \
    --depends libX11 \
    --depends libICE \
    --depends libSM \
    --depends fontconfig \
    --package "$DIST/gitgui-${VERSION}-1.${RPM_ARCH}.rpm" \
    usr

echo "==> Done:"
ls -la "$DIST"
