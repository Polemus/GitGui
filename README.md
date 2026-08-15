# Omnigit

[![CI](https://github.com/Polemus/Omnigit/actions/workflows/ci.yml/badge.svg)](https://github.com/Polemus/Omnigit/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/Polemus/Omnigit?sort=semver)](https://github.com/Polemus/Omnigit/releases/latest)
[![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)

**A desktop Git client that isn't tied to GitHub.**

Most Git GUIs are built around one hosting site and treat the rest as an afterthought.
Omnigit talks to **GitHub**, **Gitea**, **Forgejo** and **GitLab** out of the box — including
self-hosted instances behind your own domain — and to anything else you describe in a JSON
file, with no code and no plugins.

Runs on **Linux, Windows and macOS** from a single codebase. **No Git installation
required** — the native library ships inside the app.

> **Status: working, not finished.** Omnigit reads and writes real repositories and is used
> to develop itself. Pull requests are listed, checked out and opened from the app; reviewing
> and merging them, and issues, aren't implemented yet.

![Changes tab, dark theme](docs/screenshots/changes-dark.png)

## Install

**[Download the latest release →](https://github.com/Polemus/Omnigit/releases/latest)**

| Platform | Take this one |
| --- | --- |
| Debian, Ubuntu, Mint | `.deb` |
| Fedora, RHEL, openSUSE | `.rpm` |
| Any Linux, sandboxed | `.flatpak` — `flatpak install ./Omnigit-*.flatpak` |
| Any other Linux | `.AppImage` — `chmod +x` it and run it, nothing to install |
| Windows | `-setup.exe`, or the `.zip` for a portable copy |
| macOS | `.dmg` — drag Omnigit to Applications |

Both x64 and arm64 for Linux and macOS; Windows is x64.

**macOS builds are signed and notarised** — the disk image opens and the app launches with
nothing to click past. **Windows builds are unsigned**, so the first launch needs one nudge:
*More info* → *Run anyway*. For the portable `.zip` you may also need right-click the
`.exe` → *Properties* → **Unblock**.

The AppImage adds itself to your applications menu on first run, so the dock shows its own
icon rather than a generic one. `OMNIGIT_NO_DESKTOP_INTEGRATION=1` skips that.

## What it does

**Everyday Git work**

- Working-tree status with real diffs — modified, added, deleted, renamed and untracked
- Selective staging and committing, with the author from that repository's own config
- Branch creation, switching and checkout
- Fetch, push and pull, with a sync button that does whatever its label says
- Unified diff viewer with dual line-number gutters

**The bits other clients make you leave for the terminal**

- **Switching branches with uncommitted work asks first.** Bring everything, leave
  everything, or tick individual files — whatever stays behind is stashed against the
  branch you came from, and offered back when you return to it.
- **Conflicts are finished in the app.** When a merge, revert or cherry-pick stops, a panel
  lists what's stuck and offers each file three answers: keep mine, take theirs, or fix it
  by hand and mark it resolved. Committing finishes the operation; abandoning puts
  everything back.
- **A commit menu that matches GitHub Desktop's**, bar reordering — amend while it's still
  local, reset (explained in words rather than Git's), open a commit, undo it, branch from
  it, tag it, cherry-pick it onto another branch, or open it on the site it came from.
- **Discard, or add to `.gitignore`** — the file, its folder or its extension, with only the
  ones that apply actually offered.

**Living with more than one hosting site**

- **Pull requests in the branch picker.** A second tab lists the open ones for the
  repository — checking one out fetches its head and lands you on `pr/<number>`, even when
  it came from someone's fork, with no second remote to add. **Create pull request** pushes
  the branch if the site hasn't seen it yet and then opens that site's own form.
- **Browse and clone** everything your signed-in accounts can see, from every site at once
- **Repositories grouped by where they actually came from**, derived from the `origin` URL
- **Sign in with your browser** on github.com, or a personal access token anywhere
- **Tokens go to the OS keychain**, never into a settings file

**Around the edges**

- Dark and light themes, switchable at runtime
- An activity console that says what the app is doing, and expands itself on an error
- Refreshes itself when you commit in a terminal or save in an editor
- Resizable panes that remember their sizes

## How it works

**Hosting sites are JSON, not plugins.** Adding a site needs no code: **Settings → Hosting
sites → Add a site** asks which URL lists repositories and which field holds the name, tests
it against the real server, and the site works immediately without a restart. What the form
writes is an ordinary file in `~/.config/Omnigit/hosts/`, editable afterwards and identical
to one you'd write by hand — see [docs/host-manifests.md](docs/host-manifests.md).

A manifest is data and cannot execute anything. That's the point: a provider handles tokens
that can read and write all of your source code, and a compiled plugin loaded in-process
could read every one of them. Gitea and GitLab ship *as* manifests deliberately, so the
format is exercised by Omnigit's own code rather than rotting into something that only works
in theory. GitHub is C# only because its browser sign-in is a multi-step conversation that
endpoint descriptions can't express.

**Tokens never touch a settings file.**

| Platform | Where tokens live |
| --- | --- |
| Linux | system keyring via `secret-tool` (libsecret) |
| macOS | login Keychain via `security` |
| Windows | DPAPI, encrypted to your login |
| fallback | a `0600` file — the app says so plainly when it has to use this |

`accounts.json` holds only the harmless half: site, login, display name.

**No Git installation required.** LibGit2Sharp bundles native `libgit2`, and because Omnigit
publishes self-contained, it ends up inside every installer along with the .NET runtime.
One caveat: the bundled library speaks `https://` but not `ssh://`, so `git@host:` remotes
need OpenSSH present — standard on macOS and Linux, an optional feature on Windows. Both
GitHub and Gitea authenticate over HTTPS with tokens, which is the intended path.

**Built with** Avalonia 12 on .NET 10, rendering through Skia rather than wrapping native
controls, so it looks and behaves identically on all three platforms. Git via LibGit2Sharp
0.32, MVVM via CommunityToolkit.Mvvm, styling via FluentAvalonia.

## Screenshots

**History — three panes, with tags shown against the commits that carry them**

![History tab, dark theme](docs/screenshots/history-dark.png)

**Finishing a revert that stopped on conflicts** — each file gets three answers, the commit
box is pre-filled with the message Git prepared, and committing stays disabled until nothing
is conflicted

![Conflict panel, dark theme](docs/screenshots/conflicts-dark.png)

**Repository picker — grouped by the hosting site each clone actually came from**

![Repository picker](docs/screenshots/repository-picker.png)

**Light theme**

![Changes tab, light theme](docs/screenshots/changes-light.png)

## Roadmap

Roughly in order of intent:

1. **Issues, and the rest of pull requests** — check/CI status, reviewing, merging.
   Listing, checking out and opening one all work; status is where the manifest format
   stops being enough, since GitHub's check-runs, Gitea's commit statuses and GitLab's
   pipelines agree on nothing.
2. **Removing a hosting site leaves its accounts orphaned.** They stay in the list with
   nothing able to talk to them. Should warn, or sign them out.
3. **Git credential-helper fallback**, so people who already use Git on the command line
   don't have to sign in again.
4. **Reordering commits** — the one thing missing from the commit menu. libgit2 has no
   todo-list rebase, so it means driving the rebase commit by commit with conflict handling
   at every step.
5. **Paging the history**, which currently stops at 100 commits.
6. **Getting onto Flathub**, so it installs with `flatpak install` rather than from a
   downloaded file. The Flatpak already builds and ships with every release.
7. **Signing the Windows builds**, which needs a certificate from a commercial authority.

## Known limitations

- **Repository grouping assumes non-GitHub means Gitea.** `HostResolver` classifies any
  remote that isn't `github.com` as Gitea to pick a badge and colour. Sign-in doesn't work
  this way — that probes the server properly — so the consequence is a mislabelled group,
  not a failed connection.
- **Tests stop at the Git layer.** The pure functions and everything touching a repository
  are covered; the network and the whole UI are verified by hand against a real Gitea server.
- **Installers are ~45 MB**, because each build bundles the .NET runtime. `PublishTrimmed`
  would cut that substantially, but Avalonia needs trimming configuration and
  `ViewLocator`'s reflection would have to go first.
- **The screenshots above predate the rename** and still show the old name in the title bar.

## Contributing

Issues and pull requests are welcome. The roadmap and limitations above are the honest
backlog — say so in an issue before starting on one, so two people don't write it twice.

| Document | What's in it |
| --- | --- |
| [docs/building.md](docs/building.md) | Running it, the project layout, the tests, building installers, cutting a release |
| [docs/architecture.md](docs/architecture.md) | The layers and *why* they're shaped this way |
| [docs/notes.md](docs/notes.md) | Decisions that look arbitrary and aren't, plus every Avalonia, libgit2 and code-signing trap already paid for |
| [docs/host-manifests.md](docs/host-manifests.md) | How to add a hosting site by writing one JSON file |
| [docs/flatpak.md](docs/flatpak.md) | The sandbox, the checked-in NuGet lists, and how a release reaches Flathub |
| [CONTRIBUTING.md](.github/CONTRIBUTING.md) | What a good pull request looks like |
| [SECURITY.md](.github/SECURITY.md) | Reporting a vulnerability privately, and what's in scope |

Quick start:

```bash
dotnet run --project src/Omnigit/Omnigit.csproj
dotnet test
```

Found a security problem? Don't open an issue —
[report it privately](https://github.com/Polemus/Omnigit/security/advisories/new).

## Licence

MIT — see [LICENSE](LICENSE).

Release builds are self-contained, so they bundle the .NET runtime, Avalonia, Skia and
libgit2. [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) lists all of it. The one worth
knowing about is **libgit2**, which is GPLv2 — under a linking exception that explicitly
permits exactly this, so it places no obligation on Omnigit's own source or yours.
