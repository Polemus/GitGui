#!/usr/bin/env bash
#
# Prepares a release: the two edits a tag cannot carry, then the tag.
#
# Usage: build/release.sh <version> <notes> [--push]
#   version: 1.2.3, without the leading v
#   notes:   one or two sentences, in the past tense, aimed at someone reading a
#            software centre rather than a commit log
#
# A tag names a commit; it cannot put anything inside one. Two things have to be
# inside the commit because Flathub builds it without ever running our workflows:
#
#   <Version> in GitGui.csproj   what the app reports. The Flatpak build never
#                                passes -p:Version, because Flathub would not.
#   <release> in the metainfo    the notes a software centre shows. Flathub
#                                rejects a build whose newest release is not the
#                                version being built.
#
# Everything else - artifact names, the GitHub Release, the Flathub manifest - is
# derived from the tag by CI. So this exists to make the two edits stop being a
# checklist, not to move them somewhere they cannot work.
#
# Nothing is pushed unless you ask for it. Pushing the tag is what starts a
# release build, and that should be a decision rather than a side effect.
set -euo pipefail

VERSION="${1:?usage: release.sh <version> <notes> [--push]}"
NOTES="${2:?usage: release.sh <version> <notes> [--push]}"
PUSH=no
[ "${3:-}" = "--push" ] && PUSH=yes

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_ID=io.github.polemus.GitGui
CSPROJ="$ROOT/src/GitGui/GitGui.csproj"
METAINFO="$ROOT/build/linux/$APP_ID.metainfo.xml"
TAG="v$VERSION"

. "$ROOT/build/version.sh"

case "$VERSION" in
    [0-9]*.[0-9]*.[0-9]*) ;;
    *) echo "!! $VERSION is not a x.y.z version" >&2 ; exit 1 ;;
esac

# A release is built from the tagged commit, so anything not committed is not in
# it - and finding that out afterwards means retagging.
if [ -n "$(git -C "$ROOT" status --porcelain)" ]; then
    echo "!! the working tree has changes - commit or stash them first" >&2
    git -C "$ROOT" status --short >&2
    exit 1
fi

if git -C "$ROOT" rev-parse --verify "$TAG" >/dev/null 2>&1; then
    echo "!! $TAG already exists" >&2
    exit 1
fi

CURRENT="$(project_version "$ROOT")"
if [ "$CURRENT" = "$VERSION" ]; then
    echo "!! the csproj already says $VERSION - is this release already prepared?" >&2
    exit 1
fi

echo "==> GitGui $CURRENT -> $VERSION"

# ---------------------------------------------------------------- the version
# Anchored to the tag rather than a bare number: the file holds package versions
# too, and a loose match would rewrite whichever one happened to look similar.
sed -i "s|<Version>$CURRENT</Version>|<Version>$VERSION</Version>|" "$CSPROJ"

if [ "$(project_version "$ROOT")" != "$VERSION" ]; then
    echo "!! the csproj still says $(project_version "$ROOT")" >&2
    exit 1
fi

# ------------------------------------------------------------------ the notes
# Newest first, which is the order AppStream readers show them in and the order
# flathub-manifest.sh reads to check the newest is the one being built.
python3 - "$METAINFO" "$VERSION" "$NOTES" <<'PY'
import sys, textwrap

path, version, notes = sys.argv[1], sys.argv[2], sys.argv[3]
from datetime import date

body = textwrap.fill(" ".join(notes.split()), width=72,
                     initial_indent=" " * 10, subsequent_indent=" " * 10)

entry = (
    f'    <release version="{version}" date="{date.today().isoformat()}">\n'
    f'      <url type="details">'
    f'https://github.com/Polemus/GitGui/releases/tag/v{version}</url>\n'
    f'      <description>\n'
    f'        <p>\n{body}\n        </p>\n'
    f'      </description>\n'
    f'    </release>\n'
)

xml = open(path).read()
marker = "  <releases>\n"
if marker not in xml:
    sys.exit(f"no <releases> element in {path}")

open(path, "w").write(xml.replace(marker, marker + entry, 1))
PY

# appstreamcli is what Flathub runs. Catching a malformed entry here beats
# catching it in a review comment days later.
if command -v appstreamcli >/dev/null 2>&1; then
    appstreamcli validate --no-net "$METAINFO" >/dev/null \
        || { echo "!! the metainfo no longer validates" >&2 ; exit 1 ; }
    echo "==> metainfo validates"
fi

# ------------------------------------------------------------- commit and tag
git -C "$ROOT" add "$CSPROJ" "$METAINFO"
git -C "$ROOT" commit -q -m "GitGui $VERSION" -m "$NOTES"
git -C "$ROOT" tag -a "$TAG" -m "GitGui $VERSION"

echo "==> Committed and tagged $TAG"
git -C "$ROOT" --no-pager show --stat --format='    %h %s' "$TAG" | head -8

if [ "$PUSH" = yes ]; then
    echo "==> Pushing"
    git -C "$ROOT" push origin HEAD
    git -C "$ROOT" push origin "$TAG"
    echo "==> release.yml is building. Nothing else to do."
else
    echo
    echo "Nothing pushed. When you are ready:"
    echo "    git push origin HEAD && git push origin $TAG"
fi
