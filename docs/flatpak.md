# Flatpak and Flathub

Everything for the Flatpak lives in `build/linux/flatpak/`, except the two files
it shares with the `.deb`, `.rpm` and AppImage:

| File | What it is |
| --- | --- |
| `build/linux/io.github.polemus.GitGui.desktop` | The launcher. One copy, installed by all four package formats |
| `build/linux/io.github.polemus.GitGui.metainfo.xml` | AppStream metadata — the name, description and screenshots a software centre shows |
| `build/linux/flatpak/io.github.polemus.GitGui.yml` | The manifest |
| `build/linux/flatpak/nuget-sources-*.json` | Generated. Every NuGet package, pinned by hash |
| `build/linux/flatpak/package.sh` | Builds it, and writes `dist/GitGui-<version>-<arch>.flatpak` |
| `build/linux/flatpak/generate-nuget-sources.sh` | Regenerates the lists above. Needs network |
| `build/linux/flatpak/check-nuget-sources.sh` | Says whether they still match the csproj. Runs in CI |
| `build/linux/flatpak/validate.sh` | The checks Flathub runs, run here first |
| `build/linux/flatpak/flathub-manifest.sh` | Emits the copy of the manifest Flathub builds from |

The app id is **`io.github.polemus.GitGui`**, which is not a free choice: Flathub
derives it from where the source lives, and the desktop entry, the icons and the
metainfo file all have to be named after it.

## Building one

```bash
flatpak install flathub org.flatpak.Builder \
    org.freedesktop.Platform//25.08 \
    org.freedesktop.Sdk//25.08 \
    org.freedesktop.Sdk.Extension.dotnet10//25.08

./build/linux/flatpak/package.sh 0.2.0 --install
flatpak run io.github.polemus.GitGui
```

`--install` is optional; without it you get the bundle in `dist/` and nothing
installed. A `.flatpak` bundle installs with no remote involved, which is what
makes it worth attaching to a release:

```bash
flatpak install --user ./dist/GitGui-0.2.0-x86_64.flatpak
```

**The Flatpak is the one Linux artifact that cannot be cross-built.** The `.deb`,
`.rpm`, `.tar.gz` and AppImage are all produced for both architectures from one
x64 machine. A Flatpak build runs *inside* a sandbox on the runtime for the host
architecture, so each one has to come off a machine of that architecture — hence
the two-runner matrix in `release.yml`, and the `flatpak-x86_64` /
`flatpak-aarch64` groups in `build/expected-artifacts.sh`.

## The offline NuGet lists, and the trap in them

A Flatpak build has **no network**. `flatpak-builder` downloads every source
listed in the manifest first, verifies it against a hash, and only then starts
building. So `dotnet restore` cannot fetch anything: every `.nupkg` has to be
named and hashed in advance. That is all `nuget-sources-x86_64.json` and
`nuget-sources-aarch64.json` are — around forty URLs and SHA-512s each, produced
by restoring the project once per target architecture.

They are generated, and they go stale:

```bash
./build/linux/flatpak/generate-nuget-sources.sh   # needs network
```

**Run that whenever a `PackageReference` in `GitGui.csproj` changes — including
when Dependabot bumps one.** Nothing else in the repository notices. `dotnet
build` is happy, the tests pass, the `.deb` and the `.rpm` and the AppImage all
build, and the first sign of trouble is the Flatpak job failing partway through a
release with a restore error naming a package it was never given.

`check-nuget-sources.sh` exists to make that noisy instead. It runs in CI on every
push, needs no network, and checks that every direct `PackageReference` has a
`.nupkg` of exactly that version in both lists. It does not follow transitive
dependencies — that would mean doing a restore, which is the expensive online
thing it is there to avoid — but transitive versions move with direct ones often
enough that this catches nearly everything.

The two lists differ slightly, and not only in the runtime pack. The x86_64 list
has no `microsoft.netcore.app.host.linux-x64`, because the SDK extension already
ships the apphost pack for its own architecture; the aarch64 build has to
download it.

## Why the permissions are what they are

| `finish-args` | Why |
| --- | --- |
| `--share=network` | Cloning, fetching, pushing, and GitHub's device-flow sign-in |
| `--socket=x11`, `--share=ipc`, `--device=dri` | Drawing a window |
| `--talk-name=org.freedesktop.secrets` | Access tokens go to the keyring |
| `--filesystem=home`, `/mnt`, `/media`, `/run/media` | Opening repositories |

Two of those are worth the explanation.

**`--socket=x11`, not the usual `wayland` + `fallback-x11` pair.** Avalonia 12 has
no Wayland backend — it is X11 only, and reaches a Wayland desktop through
XWayland. `fallback-x11` hands over the X socket *only when the app has no
Wayland one*, so asking for both would withhold X11 from exactly the desktops
that need it, and the app would die at startup with `XOpenDisplay failed`. This
was not theoretical; it is what the first build here did. Revisit if Avalonia
grows a Wayland backend.

**`--filesystem=home` is an error to Flathub's linter,** and a deliberate one on
our side. Most apps that ask for the whole home directory do not need it; a git
client does, because repositories are wherever the user put them and libgit2
opens them by path — a portal document handle is not something it can pass to
`git_repository_open`. It is also what makes the folder picker return a real path
instead of one under `/run/user/…/doc/`. Flathub grants this as a submission
exception, and `validate.sh` lists it as expected rather than silently allowing
it, so anything *else* the linter finds still fails.

Anyone who keeps repositories somewhere none of those four cover can widen it
without a rebuild:

```bash
flatpak override --user --filesystem=host io.github.polemus.GitGui
```

## secret-tool is built here

`GitGui` stores tokens by shelling out to `secret-tool`, which talks to the
keyring over D-Bus. `org.freedesktop.Platform` ships `libsecret-1.so.0` but not
the command-line tool, so the manifest builds libsecret with everything but the
tool switched off. Without it `CredentialStoreFactory` would find no
`secret-tool`, fall back to the `0600` file store, and report `IsSecure = false`
in the accounts page — working, but not what a keyring-capable desktop should get.

## Validating

```bash
./build/linux/flatpak/validate.sh          # metainfo, desktop entry, manifest
./build/linux/flatpak/validate.sh --repo   # and the built repository
```

Three checkers that know nothing about each other: `appstreamcli`,
`desktop-file-validate` and `flatpak-builder-lint` — Flathub's own. The first two
come out of the freedesktop SDK and the third out of `org.flatpak.Builder`, so
nothing has to be installed on the host.

The `--repo` run has two failures that only ever happen locally, and are listed
as expected: `appstream-external-screenshot-url` and
`appstream-screenshots-not-mirrored-in-ostree`. Flathub's pipeline downloads the
screenshots this metainfo points at, mirrors them to `dl.flathub.org` and
rewrites the URLs into the repository. Nothing built on your machine can have
done that.

## Submitting to Flathub

Flathub builds from its own copy of the manifest, in a repository it owns, and it
clones the source itself — so its manifest cannot use the `type: dir` source that
makes a local checkout build. Rather than keep a second manifest and let the two
drift, generate it:

```bash
git tag -a v0.3.0 -m "GitGui 0.3.0"
git push origin v0.3.0

./build/linux/flatpak/flathub-manifest.sh 0.3.0
```

That writes `dist/flathub/` — the manifest with the source swapped for a `type:
git` pinned to the tag and its commit, plus the two NuGet lists. It refuses to
run against a commit that has not been pushed, because Flathub would not be able
to fetch it.

First submission:

1. Fork `github.com/flathub/flathub` and branch from `new-pr`.
2. Add the three files from `dist/flathub/` at the root.
3. Open a pull request against `new-pr`. A bot builds it and comments; a reviewer
   then looks at the permissions — expect to justify `--filesystem=home`, and
   point at the reasoning above.
4. Once merged, Flathub creates `flathub/io.github.polemus.GitGui`.

Afterwards, each release is a pull request to that repository with a regenerated
manifest. The `x-checker-data` on the libsecret source lets Flathub's update bot
propose libsecret bumps on its own.

Two things to keep in step by hand:

- **The `<releases>` block in the metainfo.** Flathub shows the newest entry as
  the release notes, and a submission whose newest release does not match the
  version being built gets rejected.
- **`<Version>` in `GitGui.csproj`.** The Flatpak build never passes
  `-p:Version`, on purpose — Flathub would not either — so the csproj is the only
  thing that decides what the app reports. `package.sh` refuses to build if the
  version you asked for and the csproj disagree.
