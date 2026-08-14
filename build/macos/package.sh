#!/usr/bin/env bash
#
# Builds macOS artifacts for one runtime identifier:
#   dist/GitGui-<version>-<rid>.dmg   drag-to-Applications disk image
#
# Usage: build/macos/package.sh <rid> [version]
#   rid: osx-arm64 (Apple silicon) | osx-x64 (Intel)
#   version: defaults to <Version> in the csproj - see build/version.sh
#
# Runs on macOS only - uses sips, iconutil and hdiutil.
#
# Signs and notarises when the APPLE_* environment variables are set, and builds
# the same unsigned artifacts it always did when they are not - see
# build/macos/sign.sh for which ones and why there is no secret naming the
# identity.
set -euo pipefail

RID="${1:?usage: package.sh <osx-arm64|osx-x64> [version]}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
. "$ROOT/build/version.sh"
. "$ROOT/build/macos/sign.sh"
VERSION="${2:-$(project_version "$ROOT")}"
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

# Signing comes after everything that writes into the bundle - the signature
# seals the contents, so a file added afterwards invalidates it. The Info.plist
# above is the last of those.
if signing_available; then
    trap signing_cleanup EXIT
    signing_prepare
    sign_bundle "$APP"

    if notarisation_available; then
        # The .app is notarised and stapled on its own, before the disk image is
        # built around it. Notarising only the .dmg would leave the copy the user
        # drags into Applications with no ticket of its own, and a first launch
        # on a machine that is offline would be refused.
        notarize "$APP"
        verify_gatekeeper "$APP"
    else
        echo "!! signed but not notarised - APPLE_API_* is not set" >&2
        echo "   Gatekeeper will still refuse this on a machine that has not seen it" >&2
    fi
else
    echo "!! building unsigned - no APPLE_CERTIFICATE_P12 in the environment" >&2
fi

echo "==> Building .dmg"
DMG_ROOT="$STAGE/dmg"
mkdir -p "$DMG_ROOT"

# ditto rather than cp -R. The bundle now carries a code signature and a stapled
# notarisation ticket, and ditto is the copy that is documented to preserve
# everything they depend on - extended attributes and all. cp is the tool people
# find out about the hard way, from a signature that verifies here and not on the
# machine that mounted the disk image.
ditto "$APP" "$DMG_ROOT/GitGui.app"
ln -s /Applications "$DMG_ROOT/Applications"

# hdiutil attaches the image while it builds it, and both RIDs ask for the same
# volume name - so the second one mounts /Volumes/GitGui <version> while the
# first is still letting go of it, and hdiutil says "Resource busy". The release
# job builds osx-arm64 and then osx-x64 in one go, which is exactly that: 0.3.0
# first failed here with the arm64 .dmg already sitting in dist/.
#
# Detach a stale one before trying, and treat a failure as worth one more go -
# the window is short and neither the volume name nor the .dmg is worth changing
# to avoid it.
VOLUME="GitGui $VERSION"

for attempt in 1 2 3; do
    if [ -d "/Volumes/$VOLUME" ]; then
        echo "==> /Volumes/$VOLUME is still mounted - detaching it"
        hdiutil detach "/Volumes/$VOLUME" -force || true
    fi

    if hdiutil create \
        -volname "$VOLUME" \
        -srcfolder "$DMG_ROOT" \
        -ov -format UDZO \
        "$DIST/GitGui-$VERSION-$RID.dmg"
    then
        break
    fi

    if [ "$attempt" -eq 3 ]; then
        echo "!! hdiutil create failed three times - giving up" >&2
        exit 1
    fi

    echo "!! hdiutil create failed - retrying in 10s (attempt $attempt of 3)" >&2
    sleep 10
done

DMG="$DIST/GitGui-$VERSION-$RID.dmg"

# And again for the disk image itself, which Gatekeeper checks on mount quite
# separately from the app inside it.
if signing_available; then
    sign_dmg "$DMG"

    if notarisation_available; then
        notarize "$DMG"
        verify_gatekeeper "$DMG"
    fi
fi

echo "==> Done:"
ls -la "$DIST"
