#!/usr/bin/env bash
#
# Checks that every artifact a release is supposed to contain is actually there.
#
# Usage:
#   build/verify-artifacts.sh <version> --dir <dir> [group]
#       Checks files on disk. Used right after packaging, on the runner that
#       built them, so a skipped format fails the job that caused it.
#
#   build/verify-artifacts.sh <version> --release <tag>
#       Checks the assets attached to a GitHub release, via gh. This is the one
#       that matters: v0.2.0 shipped without either .deb because `gh release
#       upload` exited 0 having quietly not uploaded them, and the files were
#       present on disk at the time, so only asking the release itself catches it.
#
# Exits non-zero, naming what is missing, rather than letting a partial release
# pass as a successful one.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

VERSION="${1:?usage: verify-artifacts.sh <version> --dir <dir> [group] | --release <tag>}"
MODE="${2:?expected --dir or --release}"
TARGET="${3:?expected a directory or a tag}"
GROUP="${4:-all}"

case "$MODE" in
    --dir)
        [ -d "$TARGET" ] || { echo "no such directory: $TARGET" >&2; exit 1; }
        # ls rather than find -printf, which is GNU-only and this also runs on
        # the macOS runner. dist/ is flat everywhere, so there is nothing to recurse.
        actual="$(ls -1 "$TARGET")"
        what="$TARGET"
        ;;
    --release)
        actual="$(gh release view "$TARGET" --json assets --jq '.assets[].name')"
        what="release $TARGET"
        GROUP="all"
        ;;
    *)
        echo "unknown mode: $MODE (expected --dir or --release)" >&2
        exit 1
        ;;
esac

expected="$("$ROOT/build/expected-artifacts.sh" "$VERSION" "$GROUP")"

missing=""
while IFS= read -r name; do
    [ -n "$name" ] || continue
    if ! printf '%s\n' "$actual" | grep -Fxq "$name"; then
        missing="${missing}${name}"$'\n'
        continue
    fi

    # A zero-length file is a failed build that got as far as creating its output.
    if [ "$MODE" = "--dir" ] && [ ! -s "$TARGET/$name" ]; then
        missing="${missing}${name} (present but empty)"$'\n'
    fi
done <<< "$expected"

count_expected=$(printf '%s\n' "$expected" | grep -c . || true)

if [ -n "$missing" ]; then
    echo "FAIL: $what is missing $(printf '%s' "$missing" | grep -c .) of $count_expected artifacts:" >&2
    printf '%s' "$missing" | sed 's/^/  - /' >&2
    echo >&2
    echo "What is actually there:" >&2
    printf '%s\n' "$actual" | sed 's/^/  /' >&2

    # Both of the silent-skip paths have actually happened, so name the likely
    # cause rather than leaving whoever reads the log to go and find it.
    if printf '%s' "$missing" | grep -qE '\.(deb|rpm)$'; then
        echo >&2
        echo "Missing .deb/.rpm usually means fpm was not on PATH. package.sh skips" >&2
        echo "both and still exits 0, so it stays usable on a machine without fpm." >&2
    fi

    if printf '%s' "$missing" | grep -q '\.AppImage$'; then
        echo >&2
        echo "Missing .AppImage usually means appimagetool could not be downloaded." >&2
        echo "appimage.sh treats that as a skip so the other artifacts still build." >&2
        echo "This is how v0.1.0 shipped without either AppImage." >&2
    fi

    exit 1
fi

echo "OK: all $count_expected expected artifacts present in $what"
