#!/usr/bin/env bash
#
# Builds macOS artifacts for one runtime identifier:
#   dist/GitGui-<version>-<rid>.dmg   drag-to-Applications disk image
#
# Usage: build/macos/package.sh <rid> [version]
#   rid: osx-arm64 (Apple silicon) | osx-x64 (Intel)
#
# Runs on macOS only - uses sips, iconutil and hdiutil.
# The bundle is unsigned; see README for the Gatekeeper note.
set -euo pipefail

RID="${1:?usage: package.sh <osx-arm64|osx-x64> [version]}"
VERSION="${2:-0.1.0}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DIST="$ROOT/dist"
STAGE="$ROOT/build/.stage-$RID"
APP="$STAGE/GitGui.app"

echo "==> Publishing $RID (self-contained, $VERSION)"
rm -rf "$STAGE"
mkdir -p "$DIST" "$APP/Contents/MacOS" "$APP/Contents/Resources"

dotnet publish "$ROOT/src/GitGui/GitGui.csproj" \
    --configuration Release \
    --runtime "$RID" \
    --self-contained true \
    -p:Version="$VERSION" \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    --output "$APP/Contents/MacOS"

chmod +x "$APP/Contents/MacOS/GitGui"

echo "==> Building icon set"
ICONSET="$STAGE/GitGui.iconset"
mkdir -p "$ICONSET"
SRC_PNG="$ROOT/build/linux/icons/gitgui-256.png"

# iconutil wants this exact set of names.
sips -z 16 16     "$SRC_PNG" --out "$ICONSET/icon_16x16.png"      >/dev/null
sips -z 32 32     "$SRC_PNG" --out "$ICONSET/icon_16x16@2x.png"   >/dev/null
sips -z 32 32     "$SRC_PNG" --out "$ICONSET/icon_32x32.png"      >/dev/null
sips -z 64 64     "$SRC_PNG" --out "$ICONSET/icon_32x32@2x.png"   >/dev/null
sips -z 128 128   "$SRC_PNG" --out "$ICONSET/icon_128x128.png"    >/dev/null
sips -z 256 256   "$SRC_PNG" --out "$ICONSET/icon_128x128@2x.png" >/dev/null
sips -z 256 256   "$SRC_PNG" --out "$ICONSET/icon_256x256.png"    >/dev/null
sips -z 512 512   "$SRC_PNG" --out "$ICONSET/icon_256x256@2x.png" >/dev/null
sips -z 512 512   "$SRC_PNG" --out "$ICONSET/icon_512x512.png"    >/dev/null

iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/GitGui.icns"

echo "==> Writing Info.plist"
sed -e "s/@VERSION@/$VERSION/g" \
    "$ROOT/build/macos/Info.plist.in" > "$APP/Contents/Info.plist"

echo "==> Building .dmg"
DMG_ROOT="$STAGE/dmg"
mkdir -p "$DMG_ROOT"
cp -R "$APP" "$DMG_ROOT/"
ln -s /Applications "$DMG_ROOT/Applications"

hdiutil create \
    -volname "GitGui $VERSION" \
    -srcfolder "$DMG_ROOT" \
    -ov -format UDZO \
    "$DIST/GitGui-$VERSION-$RID.dmg"

echo "==> Done:"
ls -la "$DIST"
