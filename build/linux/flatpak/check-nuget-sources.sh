#!/usr/bin/env bash
#
# Checks that the offline NuGet lists still match GitGui.csproj.
#
# Usage: build/linux/flatpak/check-nuget-sources.sh
#
# The Flatpak build has no network: it can only install packages that were named
# and hashed ahead of time in nuget-sources-<arch>.json. Bump a PackageReference
# without regenerating those and nothing anywhere complains until a release is
# already running, where the Flatpak job fails on a restore error and the other
# eleven artifacts have already been built.
#
# So this runs in CI on every push, needs no network, and takes a moment: for
# each PackageReference, is a nupkg of exactly that version in both lists?
#
# It does not check transitive dependencies. Those change with the direct ones
# often enough that catching the direct case catches nearly everything, and
# checking properly would mean doing a restore - which is the expensive, online
# thing this is here to avoid.
#
# When it fails: build/linux/flatpak/generate-nuget-sources.sh, then commit.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"

exec python3 - "$ROOT" <<'PYTHON'
import json
import re
import sys
from pathlib import Path

root = Path(sys.argv[1])
csproj = root / "src" / "GitGui" / "GitGui.csproj"
flatpak = root / "build" / "linux" / "flatpak"

# PackageReference elements, in either the one-line or the wrapping form.
references = re.findall(
    r'<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"',
    csproj.read_text(encoding="utf-8"),
)

if not references:
    sys.exit(f"no PackageReference found in {csproj} - has the file moved?")

failed = False
for arch in ("x86_64", "aarch64"):
    path = flatpak / f"nuget-sources-{arch}.json"
    if not path.is_file():
        print(f"FAIL: {path.name} does not exist")
        failed = True
        continue

    # The generator names files after the package, lowercased.
    present = {source["dest-filename"] for source in json.loads(path.read_text())}

    missing = [
        f"{name} {version}"
        for name, version in references
        if f"{name.lower()}.{version}.nupkg" not in present
    ]

    if missing:
        failed = True
        print(f"FAIL: {path.name} has no entry for:")
        for item in missing:
            print(f"  - {item}")
    else:
        print(f"OK: {path.name} covers all {len(references)} package references")

if failed:
    print()
    print("Regenerate with: build/linux/flatpak/generate-nuget-sources.sh")
    print("(it needs network, and the flatpak SDK runtimes installed)")
    sys.exit(1)
PYTHON
