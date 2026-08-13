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

## The generated manifest

Flathub builds from its own copy of the manifest, in a repository it owns, and it
clones the source itself — so its manifest cannot use the `type: dir` source that
makes a local checkout build. Rather than keep a second manifest and let the two
drift, generate it:

```bash
git tag -a v0.3.0 -m "GitGui 0.3.0"
git push origin v0.3.0

./build/linux/flatpak/flathub-manifest.sh 0.3.0
```

That writes `dist/flathub/`: the manifest with its source swapped for a `type:
git` pinned to the tag *and* its commit, plus the two NuGet lists. It refuses to
run if the commit has not been pushed — Flathub could not fetch it — or if the
newest `<release>` in the metainfo is not the version being built, which is a
rejection on Flathub's side and easy to forget.

Two versions have to agree before any of this, and both are checked:

- **`<Version>` in `GitGui.csproj`** decides what the app reports. The Flatpak
  build never passes `-p:Version`, on purpose, because Flathub would not either.
  `package.sh` refuses to build if the version you ask for and the csproj
  disagree.
- **The newest `<release>` in the metainfo** is what Flathub shows as the release
  notes. `flathub-manifest.sh` refuses to generate anything if it doesn't match.

## The one-time submission

This happens once, by hand, and only you can do it — it needs your GitHub account
and a human reviewer.

**Before starting**, tag and release the version you want to submit, and check
the build the same way Flathub will:

```bash
./build/linux/flatpak/package.sh 0.3.0 --install
flatpak run io.github.polemus.GitGui        # actually use it
./build/linux/flatpak/validate.sh --repo
```

Then:

1. **Fork `github.com/flathub/flathub`** and clone the `new-pr` branch — not
   `master`:

   ```bash
   git clone --branch=new-pr git@github.com:<you>/flathub.git
   cd flathub
   git checkout -b add-io.github.polemus.GitGui new-pr
   ```

2. **Copy in `dist/flathub/`** — the manifest and both NuGet lists — at the root
   of the repository. Nothing else: the metainfo, the desktop entry and the icons
   are installed by the build from the GitGui repository, which is where Flathub
   prefers them.

3. **Open a pull request against `new-pr`**, titled
   `Add io.github.polemus.GitGui`. Opening it against `master` is the usual
   mistake.

4. **Comment `bot, build`.** Flathub builds it for both architectures and
   comments with an installable bundle. Install that and try it — this is the
   first time anything has run the aarch64 build.

5. **Expect a question about `--filesystem=home`.** It is an error to the linter,
   and the reviewer will want the reasoning: repositories are wherever the user
   put them, libgit2 opens them by path, and a portal document handle is not
   something it can pass to `git_repository_open`. The argument in full is
   further up this page. Reviewers are volunteers; a few days of silence is
   normal.

6. **On merge**, Flathub creates `flathub/io.github.polemus.GitGui` and invites
   you as maintainer. **Accept within a week**, and note it requires 2FA on your
   GitHub account. The app appears on flathub.org within a few hours.

Afterwards, consider [verifying the app](https://docs.flathub.org/docs/for-app-authors/verification)
through your GitHub account, which is straightforward for an `io.github.*` id and
puts a verified badge on the listing.

## Automating every release after that

Updates never go through submission again. Each one is a pull request to
`flathub/io.github.polemus.GitGui` — and maintainers cannot push to its protected
branch, so a pull request is the only route regardless.

`.github/workflows/flathub.yml` opens it. It runs when the Release workflow
finishes successfully on a tag, or manually from the Actions tab with a version.
It checks out that tag, runs `flathub-manifest.sh`, commits the result to a
`gitgui-<version>` branch of the Flathub repository and opens the pull request
with `bot, build` in the body.

**It needs one secret, and does nothing until it exists.** Before the app is
accepted there is no repository to push to, so the job says so and stops rather
than failing every release.

1. Create a **fine-grained personal access token** at
   [github.com/settings/personal-access-tokens](https://github.com/settings/personal-access-tokens),
   with access to `flathub/io.github.polemus.GitGui` only, and repository
   permissions **Contents: read and write** and **Pull requests: read and write**.
   Nothing else, and nothing on this repository — the workflow reads GitGui with
   the ordinary `GITHUB_TOKEN`.
2. Add it to GitGui as the repository secret **`FLATHUB_TOKEN`**.

Then a release publishes itself as far as a pull request with a green test build,
and stops there for you to install it and merge. That last step is deliberately
manual: the test build exists to be tried, and a git client that eats somebody's
uncommitted work is not a thing to discover after publishing.

### What is deliberately *not* automated

Flathub runs an external data checker across the whole organisation every two
hours, which reads `x-checker-data` and opens update pull requests on its own.
The libsecret source has one, so libsecret bumps arrive without anyone doing
anything.

**The GitGui source deliberately does not.** A checker that noticed a new tag
would bump the commit without regenerating `nuget-sources-*.json` — and the moment
a release also changed a `PackageReference`, that pull request would be a build
failure nobody asked for. Our own workflow moves the two together or not at all.

For the same reason, don't turn on `automerge-flathubbot-prs` in a `flathub.json`
here.
