#!/usr/bin/env bash
#
# Builds a single-file AppImage from a tree already staged by package.sh:
#   dist/GitGui-<version>-<arch>.AppImage
#
# Usage: build/linux/appimage.sh <rid> [version] [stage-dir]
#   rid: linux-x64 | linux-arm64
#
# package.sh calls this at the end of its run. Call it directly to rebuild
# only the AppImage without republishing - the stage directory is reused
# as-is, so it has to exist.
#
# Requires: curl. appimagetool and the AppImage runtime are downloaded once
# into build/.tools/ and cached there.
set -euo pipefail

RID="${1:?usage: appimage.sh <linux-x64|linux-arm64> [version] [stage-dir]}"
VERSION="${2:-0.1.0}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DIST="$ROOT/dist"
STAGE="${3:-$ROOT/build/.stage-$RID}"
TOOLS="$ROOT/build/.tools"
APPDIR="$ROOT/build/.appdir-$RID"

case "$RID" in
    linux-x64)   ARCH=x86_64  ;;
    linux-arm64) ARCH=aarch64 ;;
    *) echo "unsupported rid: $RID" >&2 ; exit 1 ;;
esac

if [ ! -x "$STAGE/usr/lib/gitgui/GitGui" ]; then
    echo "!! nothing staged at $STAGE - run build/linux/package.sh $RID first" >&2
    exit 1
fi

if ! command -v curl >/dev/null 2>&1; then
    echo "!! curl not found - skipping the AppImage" >&2
    exit 0
fi

# appimagetool is itself an AppImage, and the one we can run is the one that
# matches *this* machine, whatever we are building for. The architecture of
# the output comes from $ARCH and the runtime we hand it below, so an arm64
# AppImage cross-builds fine from an x64 runner.
case "$(uname -m)" in
    x86_64)          HOST_ARCH=x86_64  ;;
    aarch64 | arm64) HOST_ARCH=aarch64 ;;
    *) echo "!! cannot run appimagetool on $(uname -m) - skipping the AppImage" >&2 ; exit 0 ;;
esac

TOOL="$TOOLS/appimagetool-$HOST_ARCH.AppImage"
RUNTIME="$TOOLS/runtime-$ARCH"

fetch() {  # fetch <url> <destination>
    local url="$1" dest="$2"
    [ -s "$dest" ] && return 0
    echo "==> Fetching $(basename "$dest")"
    mkdir -p "$(dirname "$dest")"
    if ! curl -fsSL --retry 3 -o "$dest.part" "$url"; then
        rm -f "$dest.part"
        return 1
    fi
    mv "$dest.part" "$dest"
}

if ! fetch "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$HOST_ARCH.AppImage" "$TOOL" \
   || ! fetch "https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-$ARCH" "$RUNTIME"; then
    echo "!! could not download the AppImage tooling - skipping the AppImage" >&2
    exit 0
fi
chmod +x "$TOOL"

echo "==> Laying out the AppDir"
rm -rf "$APPDIR"
mkdir -p "$APPDIR"
cp -a "$STAGE/usr" "$APPDIR/usr"

# An AppImage is launched through AppRun, so the /usr/bin symlink that the
# .deb relies on is not what starts the app here.
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
# The mount point moves on every launch, so resolve it at run time.
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/gitgui/GitGui" "$@"
APPRUN
chmod +x "$APPDIR/AppRun"

# appimagetool wants the desktop entry and the icon at the top of the AppDir, and
# the icon file has to be named after the entry's Icon= key or it refuses to
# build. Desktop integration reads the icon from .DirIcon.
APP_ID=io.github.polemus.GitGui
cp "$ROOT/build/linux/$APP_ID.desktop" "$APPDIR/$APP_ID.desktop"
cp "$ROOT/build/linux/icons/gitgui-256.png" "$APPDIR/$APP_ID.png"
cp "$APPDIR/$APP_ID.png" "$APPDIR/.DirIcon"

echo "==> Building the AppImage"
mkdir -p "$DIST"
OUT="$DIST/GitGui-$VERSION-$ARCH.AppImage"

# APPIMAGE_EXTRACT_AND_RUN unpacks appimagetool instead of mounting it, which
# is what lets it run on CI images that have no FUSE.
ARCH="$ARCH" APPIMAGE_EXTRACT_AND_RUN=1 "$TOOL" \
    --runtime-file "$RUNTIME" \
    "$APPDIR" "$OUT"

rm -rf "$APPDIR"
echo "==> Built $OUT"
