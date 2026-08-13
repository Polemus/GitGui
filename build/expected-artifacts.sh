#!/usr/bin/env bash
#
# Prints the artifacts a complete release contains, one filename per line.
#
# Usage: build/expected-artifacts.sh <version> [group]
#   group: linux | windows | macos | all   (default: all)
#
# This is the single place that knows what "complete" means. The packaging
# scripts each decide their own filenames, so this list has to agree with them;
# build/verify-artifacts.sh is what holds the two together, and it runs in CI
# right after packaging and again after the upload.
set -euo pipefail

VERSION="${1:?usage: expected-artifacts.sh <version> [linux|windows|macos|all]}"
GROUP="${2:-all}"

linux_artifacts() {
    # Two runtime identifiers, four package formats each - and the .deb and .rpm
    # names use the distro's own architecture spelling rather than the RID.
    echo "GitGui-$VERSION-linux-x64.tar.gz"
    echo "GitGui-$VERSION-linux-arm64.tar.gz"
    echo "gitgui_${VERSION}_amd64.deb"
    echo "gitgui_${VERSION}_arm64.deb"
    echo "gitgui-${VERSION}-1.x86_64.rpm"
    echo "gitgui-${VERSION}-1.aarch64.rpm"
    echo "GitGui-$VERSION-x86_64.AppImage"
    echo "GitGui-$VERSION-aarch64.AppImage"
}

windows_artifacts() {
    echo "GitGui-$VERSION-win-x64-setup.exe"
    echo "GitGui-$VERSION-win-x64.zip"
}

macos_artifacts() {
    echo "GitGui-$VERSION-osx-arm64.dmg"
    echo "GitGui-$VERSION-osx-x64.dmg"
}

case "$GROUP" in
    linux)   linux_artifacts ;;
    windows) windows_artifacts ;;
    macos)   macos_artifacts ;;
    all)     linux_artifacts; windows_artifacts; macos_artifacts ;;
    *) echo "unknown group: $GROUP (expected linux, windows, macos or all)" >&2; exit 1 ;;
esac
