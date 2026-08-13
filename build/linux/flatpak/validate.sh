#!/usr/bin/env bash
#
# Runs the checks Flathub runs, before Flathub runs them.
#
# Usage: build/linux/flatpak/validate.sh [--repo]
#   --repo  also lint build/.flatpak-repo, which only exists after package.sh
#
# Three separate checkers, none of which knows about the others:
#   appstreamcli          the metainfo XML is well-formed and complete
#   desktop-file-validate the desktop entry is a valid one
#   flatpak-builder-lint  Flathub's own linter, on the manifest
#
# The first two live in the freedesktop SDK and the third in org.flatpak.Builder,
# so this reaches into both rather than asking for anything to be installed on
# the host. Everything it checks is shared with the .deb, .rpm and AppImage, so
# it is worth running after touching the desktop entry or the metainfo even if
# you are not building a Flatpak.
set -euo pipefail

# Every check below is a `flatpak run`, and none of them work without a session
# bus - see the note in docs/notes.md. A CI runner has none, so make one and
# start again inside it.
if [ -z "${DBUS_SESSION_BUS_ADDRESS:-}" ] && command -v dbus-run-session >/dev/null 2>&1; then
    exec dbus-run-session -- "$0" "$@"
fi

APP_ID=io.github.polemus.GitGui
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
SDK=org.freedesktop.Sdk//25.08

METAINFO="$ROOT/build/linux/$APP_ID.metainfo.xml"
DESKTOP="$ROOT/build/linux/$APP_ID.desktop"
MANIFEST="$ROOT/build/linux/flatpak/$APP_ID.yml"

status=0
run() {  # run <label> <command...>
    echo "==> $1"
    if "${@:2}"; then
        echo "    ok"
    else
        echo "    FAILED" >&2
        status=1
    fi
}

# --no-net: this has to pass on a machine with no network, and whether
# github.com is reachable today is not what is being tested here. --strict makes
# a warning fail the run, which is the bar Flathub holds submissions to.
run "appstreamcli validate" \
    flatpak run --filesystem="$ROOT:ro" --command=appstreamcli "$SDK" \
        validate --explain --no-net --strict "$METAINFO"

run "desktop-file-validate" \
    flatpak run --filesystem="$ROOT:ro" --command=desktop-file-validate "$SDK" \
        "$DESKTOP"

# Flathub's own linter, minus the complaints we have already argued with. Each
# one is named here rather than silently allowed, so anything the linter finds
# that is NOT on the list still fails the run.
#
# The mount is writable, not read-only: the linter resolves a manifest by running
# flatpak-builder --show-manifest, which insists on making a state directory
# whether or not it is going to build anything.
lint() {  # lint <artifact-type> <path> [expected-error ...]
    local kind="$1" path="$2"
    shift 2

    echo "==> flatpak-builder-lint $kind"

    local report
    report="$(flatpak run --filesystem="$ROOT" \
        --command=flatpak-builder-lint org.flatpak.Builder "$kind" "$path" 2>&1 || true)"

    if python3 - "$report" "$@" <<'PYTHON'
import json
import sys

report, expected = sys.argv[1], set(sys.argv[2:])

try:
    parsed = json.loads(report or "{}")
except json.JSONDecodeError:
    print(report)
    print("    the linter did not return a report")
    sys.exit(1)

errors = set(parsed.get("errors", []))

for name in sorted(errors & expected):
    print(f"    expected: {name}")
for name in sorted(errors - expected):
    print(f"    {name}")
for warning in sorted(parsed.get("warnings", [])):
    print(f"    warning: {warning}")

sys.exit(1 if errors - expected else 0)
PYTHON
    then
        echo "    ok"
    else
        echo "    FAILED" >&2
        status=1
    fi
}

# finish-args-home-filesystem-access is an error on Flathub by default, because
# most apps asking for the whole home directory do not need it. A git client
# does: repositories are wherever the user keeps them, and libgit2 opens them by
# path. Flathub grants it as a submission exception.
HOME_ACCESS=finish-args-home-filesystem-access

lint manifest "$MANIFEST" "$HOME_ACCESS"

if [ "${1:-}" = "--repo" ]; then
    REPO="$ROOT/build/.flatpak-repo"
    if [ -d "$REPO" ]; then
        # Every screenshot complaint here is an artefact of building outside
        # Flathub. Their pipeline downloads the screenshots this metainfo points
        # at, mirrors them to dl.flathub.org and rewrites the URLs into the repo;
        # a local build reaches raw.githubusercontent.com or it doesn't, and
        # which of the three you get depends on how much of that succeeded. None
        # of it says anything about the manifest.
        #
        # Allowing appstream-missing-screenshots is safe because it is not the
        # check that would catch a metainfo with no screenshots in it - the
        # appstreamcli run above is, and it does not need network to do it.
        lint repo "$REPO" \
            "$HOME_ACCESS" \
            appstream-external-screenshot-url \
            appstream-screenshots-not-mirrored-in-ostree \
            appstream-missing-screenshots
    else
        echo "!! $REPO does not exist - run build/linux/flatpak/package.sh first" >&2
        status=1
    fi
fi

exit "$status"
