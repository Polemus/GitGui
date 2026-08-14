#!/usr/bin/env bash
#
# Builds the Flatpak and writes a single-file bundle:
#   dist/GitGui-<version>-<arch>.flatpak
#
# Usage: build/linux/flatpak/package.sh [version] [--install]
#   --install  also install the bundle for the current user, so you can run it
#
# A .flatpak bundle installs without a remote:
#     flatpak install --user ./GitGui-<version>-x86_64.flatpak
# which is what the release attaches. Flathub installs the same manifest from
# its own build servers instead - see docs/flatpak.md.
#
# The architecture is whatever this machine is. Unlike the AppImage and the
# .deb, a Flatpak cannot be cross-built: the build runs inside a sandbox using
# the runtime for the host architecture.
#
# Requires: flatpak, and org.flatpak.Builder. Both are installed from Flathub:
#     flatpak install flathub org.flatpak.Builder
set -euo pipefail

# Nothing here can run a Flatpak without a session bus - see the note in
# docs/notes.md. A CI runner has none, so make one and start again inside it.
if [ -z "${DBUS_SESSION_BUS_ADDRESS:-}" ] && command -v dbus-run-session >/dev/null 2>&1; then
    exec dbus-run-session -- "$0" "$@"
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"

. "$ROOT/build/version.sh"
CSPROJ_VERSION="$(project_version "$ROOT")"

VERSION="${1:-$CSPROJ_VERSION}"
INSTALL=no
[ "${2:-}" = "--install" ] && INSTALL=yes

APP_ID=io.github.polemus.GitGui
HERE="$ROOT/build/linux/flatpak"
DIST="$ROOT/dist"
STATE="$ROOT/build/.flatpak-builder"
REPO="$ROOT/build/.flatpak-repo"
MANIFEST="$HERE/$APP_ID.yml"

case "$(uname -m)" in
    x86_64)          ARCH=x86_64  ;;
    aarch64 | arm64) ARCH=aarch64 ;;
    *) echo "!! no Flatpak runtime for $(uname -m)" >&2 ; exit 1 ;;
esac

if ! command -v flatpak >/dev/null 2>&1; then
    echo "!! flatpak not found - skipping the Flatpak" >&2
    exit 0
fi

# org.flatpak.Builder is itself a Flatpak, which is how this works the same way
# on a distro whose flatpak-builder package is too old to know about 25.08 - and
# it means the build a release runs is the same one you ran locally.
BUILDER=(flatpak run org.flatpak.Builder)
if ! flatpak info org.flatpak.Builder >/dev/null 2>&1; then
    if command -v flatpak-builder >/dev/null 2>&1; then
        BUILDER=(flatpak-builder)
    else
        echo "!! neither org.flatpak.Builder nor flatpak-builder found - skipping the Flatpak" >&2
        echo "   install it with: flatpak install flathub org.flatpak.Builder" >&2
        exit 0
    fi
fi

if [ ! -s "$HERE/nuget-sources-$ARCH.json" ]; then
    echo "!! nuget-sources-$ARCH.json is missing or empty" >&2
    echo "   run build/linux/flatpak/generate-nuget-sources.sh (it needs network)" >&2
    exit 1
fi

# Only reachable when a version was passed explicitly, since the default is the
# csproj's. Release CI always passes one, which is the case this is for.
if [ "$CSPROJ_VERSION" != "$VERSION" ]; then
    echo "!! asked for $VERSION but src/GitGui/GitGui.csproj says $CSPROJ_VERSION" >&2
    echo "   the Flatpak build has no way to override it - update the csproj first" >&2
    exit 1
fi

echo "==> Building $APP_ID $VERSION for $ARCH"
mkdir -p "$DIST"

# --force-clean throws away the previous build tree; the download and build
# caches under $STATE survive it, so a rebuild does not re-fetch 43 nupkgs.
"${BUILDER[@]}" \
    --force-clean \
    --disable-rofiles-fuse \
    --repo="$REPO" \
    --state-dir="$STATE" \
    "$STATE/build" \
    "$MANIFEST"

echo "==> Exporting the bundle"
BUNDLE="$DIST/GitGui-$VERSION-$ARCH.flatpak"

# build-bundle looks for the "master" branch unless told otherwise, while
# flatpak-builder exported whatever default-branch in the manifest says. Read it
# from there rather than repeating it here, or the two drift and the export fails
# with "Refspec not found" at the very end of a twenty-minute build.
BRANCH="$(sed -n 's/^default-branch: *//p' "$MANIFEST")"
BRANCH="${BRANCH:-master}"

flatpak build-bundle "$REPO" "$BUNDLE" "$APP_ID" "$BRANCH" --arch="$ARCH"

if [ "$INSTALL" = yes ]; then
    echo "==> Installing for the current user"
    flatpak install --user --noninteractive --reinstall "$BUNDLE"
    echo "==> Run it with: flatpak run $APP_ID"
fi

echo "==> Built $BUNDLE"
ls -la "$BUNDLE"
