#!/usr/bin/env bash
#
# Prints one version's release notes as markdown, read from the metainfo.
#
# Usage: build/release-notes.sh [version]        (default: the csproj's)
#
# build/release.sh already writes a <release> entry for every version, because a
# software centre shows it and Flathub builds the commit rather than the tag. The
# GitHub release used to ignore all of that and print the same fixed blurb every
# time, so the one place a user looks for "what changed" was the only place that
# never said. This is the bridge, and it deliberately reads the metainfo rather
# than introducing a notes file of its own - a second copy of the notes is a copy
# that gets forgotten, exactly like a second copy of the version.
#
# Exits non-zero when the version has no entry, which is a release.sh that did not
# run rather than a release with nothing to say.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
. "$ROOT/build/version.sh"

VERSION="${1:-$(project_version "$ROOT")}"
METAINFO="$ROOT/build/linux/io.github.polemus.Omnigit.metainfo.xml"

python3 - "$METAINFO" "$VERSION" <<'PY'
import sys
import xml.etree.ElementTree as ET

path, version = sys.argv[1], sys.argv[2]

root = ET.parse(path).getroot()
entry = next(
    (r for r in root.findall("./releases/release") if r.get("version") == version),
    None,
)

if entry is None:
    sys.exit(f"no <release version=\"{version}\"> in {path} - did build/release.sh run?")

description = entry.find("description")
if description is None:
    sys.exit(f"the {version} entry in {path} has no <description>")


def text(node):
    """The element's whole text, whitespace collapsed. AppStream wraps <p> bodies
    across lines for legibility; markdown wants one paragraph per line."""
    return " ".join("".join(node.itertext()).split())


lines = []
for node in description:
    if node.tag == "p":
        lines.append(text(node))
    elif node.tag in ("ul", "ol"):
        # Numbered lists render as bullets: markdown renumbers its own, and the
        # ordering an AppStream <ol> carries is not meaningful in a changelog.
        lines.extend(f"- {text(item)}" for item in node.findall("li") if text(item))

lines = [line for line in lines if line]
if not lines:
    sys.exit(f"the {version} entry in {path} describes nothing")

print("\n\n".join(lines))
PY
