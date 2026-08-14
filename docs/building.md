# Building, testing and releasing

Everything a contributor or maintainer needs and a user doesn't. The README covers
what Omnigit is and how to install it.

## Running it

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/Omnigit/Omnigit.csproj
```

In VS Code, <kbd>F5</kbd> works via the checked-in `.vscode/launch.json`. (If you press
F5 with a `.axaml` file focused *before* that config exists, VS Code offers to find an
"Avalonia XAML debugger" — decline it; no such extension is needed.)

## Project layout

```
src/Omnigit/
  Models/        Domain types — hosts, accounts, repos, commits, diffs
  Services/
    GitService.cs          libgit2 implementation of IGitService
    GitService.CommitOperations.cs
                           tags, reverts, cherry-picks, resets and conflicts
    UnifiedDiffParser.cs   libgit2 patch text -> renderable diff rows
    HostResolver.cs        origin URL -> which hosting site a clone belongs to
    WebLinks.cs            clone + commit -> the page to open in a browser
    AccountStore.cs        accounts split between JSON and the keyring
    CredentialStore.cs     keyring / Keychain / DPAPI, with a 0600 fallback
    ActivityLog.cs         what the console shows
    RepositoryStore.cs     known-clone list, JSON in the app-data dir
    RepositoryWatcher.cs   notices work done outside the app, debounced
    DesktopIntegration.cs  the AppImage's own .desktop entry and icons
    FolderPicker.cs        native folder dialog via StorageProvider
    SystemShell.cs         open a browser, reach the clipboard
    MockData.cs            design-time sample content only
  HostProviders/ GitHub in C#, everything else from a JSON manifest
  ViewModels/    MainWindowViewModel + per-item view models and prompts
  Views/         MainWindow, RepositoryView, DiffView, CloneView, SettingsView
  Styles/        Tokens.axaml (theme colours + icons), Controls.axaml (control styles)
  Assets/        App icon
tests/Omnigit.Tests/
                 xunit — pure functions, plus branch switching, commit operations
                 and conflicts against real throwaway repositories
build/
  version.sh     the one place the version is read from
  release.sh     bump, write release notes, commit, tag
  linux/         package.sh, .desktop entry, hicolor icons, AppImage, Flatpak
  windows/       package.ps1, Inno Setup script
  macos/         package.sh, sign.sh, entitlements, Info.plist template
.github/workflows/
  ci.yml         Linux build on push/PR
  release.yml    Full platform matrix, tag-triggered
```

All colours are defined once in `Styles/Tokens.axaml`, per theme variant, and referenced
with `DynamicResource`. To restyle the app, that's the only file you need.

## Tests

```bash
dotnet test
```

Some cover the pure functions, which are the parts that break silently: the unified-diff
parser, the remote-URL resolution behind repository grouping, the commit-link builder, the
`Link`-header paging that repository listing depends on, and the manifest field mapping —
including the round trip through the settings form, since a lost field there would quietly
mislabel every repository on a site.

The rest run against **real repositories** created in a temp directory, because what makes
them worth writing is what libgit2 actually leaves behind:

- **Branch switching.** Carrying only some uncommitted files across a checkout takes two
  stashes and six steps, and a mistake there loses work that was never committed — the one
  place in this codebase where a bug is unrecoverable, so it is verified rather than
  reasoned about.
- **Commit operations.** Tagging, branching from an older commit, detaching onto a commit,
  reverting, cherry-picking and all three reset modes — each asserted on the state left in
  the repository, including the half-applied ones.
- **Conflicts.** Keeping either side, marking resolved by hand, finishing with a commit and
  abandoning outright, each checked against the index rather than the working tree.
- **Sync state.** What the sync button is told about a branch, including one pushed without
  an upstream, against a bare "origin" beside the working copy.

LibGit2Sharp bundles its own native library, so none of this needs git installed.

Nothing here talks to the network or the UI. Those are still checked by hand against a real
Gitea server — see the docker one-liner in [architecture.md](architecture.md).

## Building installers

Each script publishes a **self-contained** build, so end users don't need .NET installed.

```bash
# Linux — .deb, .rpm, .tar.gz and .AppImage  (needs fpm: gem install fpm)
./build/linux/package.sh linux-x64
./build/linux/package.sh linux-arm64

# Linux — .flatpak  (needs flatpak: flatpak install flathub org.flatpak.Builder)
./build/linux/flatpak/package.sh

# macOS — .app bundle inside a .dmg  (run on macOS)
./build/macos/package.sh osx-arm64

# Windows — portable .zip and an Inno Setup installer
pwsh build/windows/package.ps1 -Rid win-x64
```

Artifacts land in `dist/`. None of those commands names a version: every one of them
defaults to `<Version>` in `Omnigit.csproj`, which is the single place it is written
down. Pass one as the last argument to override it, as release CI does.

The macOS build signs and notarises itself when the `APPLE_*` variables are set — see the
list at the top of `build/macos/sign.sh` — and produces the same unsigned bundle when they
aren't, so a build on a machine without the certificate still works. It is also the one
target published with `PublishSingleFile`, because `Contents/MacOS` is a code location and
codesign will not seal a bundle with loose managed assemblies in it.

The AppImage is built by `build/linux/appimage.sh`, which `package.sh` calls once the
publish is staged. `appimagetool` and the AppImage runtime are downloaded on first use and
cached in `build/.tools/`; without network access that step is skipped and the other three
artifacts are still produced. Run the script on its own to rebuild only the AppImage —
it reuses whatever `package.sh` last staged instead of publishing again:

```bash
./build/linux/appimage.sh linux-x64
```

The architecture of the output comes from the runtime handed to `appimagetool`, not from
the machine running it, so the arm64 AppImage cross-builds from an x64 runner like the
rest of the Linux artifacts.

**The Flatpak is the exception to that.** Its build runs inside a sandbox on the runtime
for the host architecture, so it cannot be cross-built and each architecture needs a
runner of its own. It also builds with no network at all, which means every NuGet package
has to be listed and hashed in advance — so **bumping a `PackageReference` means
regenerating `build/linux/flatpak/nuget-sources-*.json`**, and CI checks that you did.
[flatpak.md](flatpak.md) covers that, the permissions the sandbox asks for and why, and
how a release reaches Flathub.

## Releasing

```bash
./build/release.sh 1.2.3 "What changed, in a sentence or two."
git push origin HEAD && git push origin v1.2.3
```

That bumps `<Version>` in `Omnigit.csproj`, adds a `<release>` entry to the metainfo,
commits and tags. Those two edits are all a release needs, and both have to be *inside*
the tagged commit rather than derived from the tag: the csproj is what the app reports
and what the Flatpak build reads (it never passes `-p:Version`, because Flathub would
not either), and the metainfo entry is the release notes a software centre shows —
Flathub rejects a build whose newest `<release>` isn't the version being built. A tag
names a commit; it can't put anything in one.

Everything else is derived from the tag by CI: artifact names, the GitHub Release, the
Flathub manifest. Nothing is pushed until you say so, since pushing the tag is what
starts a forty-minute build.

Two guards, because both edits are the kind you can forget: `release.sh` refuses a
dirty tree, a version that already exists, or one the csproj already claims, and
`release.yml` refuses to build at all if the tag and the csproj disagree — rather than
shipping fourteen artifacts labelled one version by an app that calls itself another.

`release.yml` runs the tests first — `ci.yml` only triggers on pushes to a branch, so
without that gate a tag would package untested code — then builds every target and
attaches the results to a GitHub Release:

| Platform | Artifacts |
| --- | --- |
| Linux x64 / arm64 | `.deb`, `.rpm`, `.tar.gz`, `.AppImage` |
| Linux x86_64 / aarch64 | `.flatpak`, one runner each — it is the one thing here that can't cross-build |
| Windows x64 | `-setup.exe`, `.zip` |
| macOS arm64 / x64 | `.dmg`, signed and notarised |

You can also run it manually from the Actions tab and pass a version.

Once Omnigit is on Flathub, `flathub.yml` picks up from there: a successful release
opens the pull request that publishes it, leaving the test build for a human to install
and merge. It needs one repository secret and does nothing at all until that exists —
[flatpak.md](flatpak.md) has both the one-time submission and the token.

**Every release is checked against a list of what it should contain.**
`build/expected-artifacts.sh` names the fourteen files a complete release has, and
`build/verify-artifacts.sh` enforces it — once on each runner right after packaging,
once when every set is gathered together, and once against the published release
itself. That last one is the one that matters: every packaging script treats a missing
tool as a skip and still exits 0, and `gh release upload` has exited 0 having quietly not
uploaded two of them. Every one of those failures has happened, and all of them
looked like a green build.

**On CI shape:** the release workflow runs **only** on tags or manual dispatch, and
cross-publishes both Linux RIDs from one Linux runner and both macOS RIDs from one macOS
runner. Only the Flatpak needs a runner per architecture, because it is the one artifact
that cannot be cross-built. Everyday CI is Linux-only and build-only, because this is
one codebase with no per-platform branches, so a three-platform matrix on every push
would mostly buy you a longer wait for the same answer — plus a few seconds validating
the desktop entry, the AppStream file and the Flatpak's NuGet lists, none of which the
build would ever notice were wrong.
