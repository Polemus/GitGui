#!/usr/bin/env bash
#
# Serves a directory of artifacts as if it were a GitHub release, so the in-app
# updater can be watched doing the whole thing - check, download, verify, swap,
# relaunch - without publishing anything to anybody.
#
# Usage: build/fake-release.sh <version> [directory] [port]
#   version:   what the release calls itself, e.g. 0.4.0
#   directory: where the artifacts are (default: dist)
#   port:      default 8765
#
# Then run Omnigit against it:
#   OMNIGIT_UPDATE_API=http://localhost:8765/ ./Omnigit-0.3.0-x86_64.AppImage
#
# It answers the one endpoint the updater asks for, in GitHub's shape, and serves
# every file in the directory as an asset. SHA256SUMS is computed on the way out
# rather than read off disk, which is the point: the updater refuses anything
# whose hash is not published, and a stale manifest would look like a bug in the
# updater instead of a stale manifest.
set -euo pipefail

VERSION="${1:?usage: fake-release.sh <version> [directory] [port]}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIR="$(cd "${2:-$ROOT/dist}" && pwd)"
PORT="${3:-8765}"

[ -n "$(ls -A "$DIR" 2>/dev/null)" ] || { echo "!! $DIR is empty" >&2; exit 1; }

echo "==> Serving $DIR as release v$VERSION on http://localhost:$PORT/"
echo "    OMNIGIT_UPDATE_API=http://localhost:$PORT/"
echo

VERSION="$VERSION" DIR="$DIR" PORT="$PORT" python3 - <<'PY'
import hashlib
import json
import os
import re
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

VERSION = os.environ["VERSION"]
DIR = os.environ["DIR"]
PORT = int(os.environ["PORT"])
TAG = f"v{VERSION}"

# Everything in the directory except the manifest, which is generated below so it
# can never disagree with what is actually being served.
FILES = sorted(
    name for name in os.listdir(DIR)
    if os.path.isfile(os.path.join(DIR, name)) and name != "SHA256SUMS"
)


def checksums():
    lines = []
    for name in FILES:
        digest = hashlib.sha256()
        with open(os.path.join(DIR, name), "rb") as handle:
            for block in iter(lambda: handle.read(1 << 20), b""):
                digest.update(block)
        lines.append(f"{digest.hexdigest()}  {name}")
    return "\n".join(lines) + "\n"


MANIFEST = checksums().encode()


def release(host):
    def asset(name, size):
        return {
            "name": name,
            "size": size,
            "browser_download_url": f"http://{host}/download/{name}",
        }

    assets = [asset(n, os.path.getsize(os.path.join(DIR, n))) for n in FILES]
    assets.append(asset("SHA256SUMS", len(MANIFEST)))

    return {
        "tag_name": TAG,
        "name": f"Omnigit {VERSION}",
        "html_url": f"http://{host}/releases/tag/{TAG}",
        # The same shape release.yml builds: what changed, then the standing
        # sections. Summarise() should show only the first paragraph.
        "body": (
            f"A pretend release, served from {DIR}. If you can read this in the "
            f"About tab, the notes came off the feed.\n\n"
            f"## Install\n\nThis half should not appear in the app.\n"
        ),
        "assets": assets,
    }


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        host = self.headers.get("Host", f"localhost:{PORT}")

        if re.fullmatch(r"/repos/[^/]+/[^/]+/releases/latest", self.path):
            return self.send_json(release(host))

        if self.path.startswith("/download/"):
            return self.send_asset(self.path[len("/download/"):])

        self.send_error(404, f"no route for {self.path}")

    def send_json(self, payload):
        body = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def send_asset(self, name):
        if name == "SHA256SUMS":
            return self.send_bytes(MANIFEST)

        # No path traversal: only the files listed at startup are servable.
        if name not in FILES:
            return self.send_error(404, f"no asset named {name}")

        path = os.path.join(DIR, name)
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(os.path.getsize(path)))
        self.end_headers()

        with open(path, "rb") as handle:
            for block in iter(lambda: handle.read(1 << 20), b""):
                self.wfile.write(block)

    def send_bytes(self, body):
        self.send_response(200)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format, *args):
        print(f"    {self.address_string()} {format % args}", flush=True)


print(f"    {len(FILES)} asset(s) + SHA256SUMS")
for name in FILES:
    print(f"      {name}")
print(flush=True)

ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
PY
