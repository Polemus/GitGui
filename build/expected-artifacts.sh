#!/usr/bin/env bash
#
# Prints the artifacts a complete release contains, one filename per line.
#
# Usage: build/expected-artifacts.sh <version> [group]
#   group: linux | flatpak | flatpak-x86_64 | flatpak-aarch64
#          | windows | macos | all          (default: all)
#
# The Flatpak is split by architecture because, unlike everything else on Linux,
# it cannot be cross-built - the build runs inside a sandbox using the runtime
# for the host architecture, so each one comes off its own runner and each runner
# can only be asked about its own.
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
    echo "Omnigit-$VERSION-linux-x64.tar.gz"
    echo "Omnigit-$VERSION-linux-arm64.tar.gz"
    echo "omnigit_${VERSION}_amd64.deb"
    echo "omnigit_${VERSION}_arm64.deb"
    echo "omnigit-${VERSION}-1.x86_64.rpm"
    echo "omnigit-${VERSION}-1.aarch64.rpm"
    echo "Omnigit-$VERSION-x86_64.AppImage"
    echo "Omnigit-$VERSION-aarch64.AppImage"
}

flatpak_artifacts() {  # flatpak_artifacts <flatpak-arch>
    echo "Omnigit-$VERSION-$1.flatpak"
}

windows_artifacts() {
    echo "Omnigit-$VERSION-win-x64-setup.exe"
    echo "Omnigit-$VERSION-win-x64.zip"
}

macos_artifacts() {
    echo "Omnigit-$VERSION-osx-arm64.dmg"
    echo "Omnigit-$VERSION-osx-x64.dmg"
}

case "$GROUP" in
    linux)           linux_artifacts ;;
    flatpak)         flatpak_artifacts x86_64; flatpak_artifacts aarch64 ;;
    flatpak-x86_64)  flatpak_artifacts x86_64 ;;
    flatpak-aarch64) flatpak_artifacts aarch64 ;;
    windows)         windows_artifacts ;;
    macos)           macos_artifacts ;;
    all)
        linux_artifacts
        flatpak_artifacts x86_64
        flatpak_artifacts aarch64
        windows_artifacts
        macos_artifacts
        ;;
    *)
        echo "unknown group: $GROUP" >&2
        echo "expected linux, flatpak, flatpak-x86_64, flatpak-aarch64, windows, macos or all" >&2
        exit 1
        ;;
esac
