# Decisions and gotchas

Two lists. The first is choices that look arbitrary from the outside and aren't — if you
are about to change one, this is the argument you'd be arguing against. The second is
traps in Avalonia and libgit2 that each cost real time to find. Nothing here is theory;
all of it was paid for once already.

[`architecture.md`](architecture.md) covers the layers and how they fit together. This
file covers why.

---

## Decisions worth not re-litigating

**Avalonia, not Tauri or Electron.** It renders with Skia, so the app looks identical on
Linux, Windows and macOS rather than inheriting three different sets of native control
quirks — and it's C# end to end instead of a webview stapled to a backend.

**Hosting sites are JSON manifests, not plugin DLLs.** A provider handles tokens that can
read and write all of a user's source code. A compiled plugin loaded in-process could
read every token and exfiltrate them, with no practical way to sandbox it. A manifest is
data and cannot execute anything. GitHub is C# only because its browser device login is a
multi-step conversation that can't be expressed as endpoint descriptions. Gitea and
GitLab ship *as manifests* deliberately, so the format is exercised by our own code
rather than only by other people's. See [host-manifests.md](host-manifests.md).

**Expected conditions return values; faults throw.** Being signed out is ordinary for a
git client, so network operations return a `SyncResult`. Modelling it as an exception
both muddled the control flow and made the debugger halt on every occurrence.

**Tokens never touch a settings file.** `AccountStore` splits an account in two: the
harmless half goes to `accounts.json`, the token to `ICredentialStore` — keyring on
Linux, Keychain on macOS, DPAPI on Windows, with a `0600` file fallback that reports
`IsSecure = false` so the UI can warn rather than pretending.

**The GitHub OAuth client id is checked in, and that is not a leak.** `DefaultClientId`
in `GitHubProvider` is Omnigit's own OAuth App on github.com. A client id names the
application on the approval screen and authorises nothing by itself; the device flow is a
public-client grant with no client secret, so there is no second half to protect.
Shipping it is what lets browser sign-in work out of the box instead of asking every user
to register their own app. It is gated to github.com — an Enterprise server has never
heard of it, so those still need `OMNIGIT_GITHUB_CLIENT_ID`, which overrides the default
everywhere.

**libgit2 handles are opened per call, not cached.** They aren't thread-safe and the UI
calls in from pooled threads. Opening per call is cheap next to the work each one does.

**Commit operations refuse a dirty working tree rather than stashing round it.** Revert
and cherry-pick both stop mid-way on conflicts, and telling git's conflict markers apart
from work already in the tree afterwards is guesswork. `SwitchBranch` earns its stash
dance because switching branches with changes in flight is routine; reverting with them
is not.

**Cherry-pick switches to the target branch first, and stays there.** A cherry-pick
applies to whatever HEAD is on — there is no "apply to that branch from here" in git. If
it conflicts, the half-applied state is on the target branch, which is exactly where the
user needs to be to finish it.

---

## Gotchas already paid for

### Avalonia

**Never hand-write `InitializeComponent`.** Avalonia's source generator emits one that
also assigns the `x:Name` fields. Writing your own shadows it, the fields stay null, and
you get a `NullReferenceException` in the constructor with no obvious cause.

**`Button` raises `Click` *before* invoking `Command`.** Hiding a flyout in a `Click`
handler tears down the popup's visual tree, which detaches the row's DataContext and
makes `CommandParameter` re-evaluate to **null** before the command runs. Defer the hide
with `Dispatcher.UIThread.Post`.

**`x:Name` on a `Flyout` generates no field** — it isn't a Control. Name the button
instead and use `TheButton.Flyout?.Hide()`.

**Anything bound with compiled bindings needs `x:DataType` in scope.** A binding inside a
control whose DataContext you reassigned resolves against the *new* type. Put visibility
tests on a wrapper element that still has the view model as its DataContext.

**Style selectors match the exact type, not derived ones.** `Selector="TextBlock.mono"`
does *not* style a `SelectableTextBlock`, which derives from it — the class silently does
nothing and you get the default font. Use `:is(TextBlock).mono` to include subclasses, or
set the property directly on the control.

**A string containing `{` in XAML is read as a markup extension.** A `PlaceholderText`
holding a URL template needs the empty-extension escape:
`"{}{base}/{owner}/{repo}"`.

**Two theming systems must be kept in step.** Our own styles read `AccentBrush` from
`Styles/Tokens.axaml`; stock controls — CheckBox, Slider, focus rings — read
`SystemAccentColor*` from FluentAvalonia. `App.Initialize()` pushes the
`BrandAccentColor` token into `FluentAvaloniaTheme.CustomAccentColor` so one value drives
both. Changing the accent means editing `BrandAccentColor` and nothing else.

**Avalonia 12 renames.** `TextBox.Watermark` is now `PlaceholderText`.
`Avalonia.Diagnostics` is now `AvaloniaUI.DiagnosticsSupport`. The clipboard moved to
`IDataTransfer`, so `IClipboard.SetTextAsync` is an extension method in
`Avalonia.Input.Platform` and needs that using directive.

### libgit2

**Don't throw from inside a libgit2 callback.** The exception has to travel back out
through native frames; the debugger reports it as unhandled in user code and breaks every
time. Record what happened and decide once you're back in managed code.

**`ActivityLog` writes must be marshalled to the UI thread.** libgit2 progress callbacks
fire on whichever thread happens to be transferring.

**LibGit2Sharp exposes no `git_repository_state_cleanup`.** Committing goes through
`Repository.Commit`, which calls it internally, so *finishing* a revert or a merge cleans
up after itself. *Abandoning* one does not — `AbortOperation` deletes `MERGE_HEAD`,
`REVERT_HEAD`, `CHERRY_PICK_HEAD`, `MERGE_MSG`, `MERGE_MODE` and `sequencer/` by hand.
Miss one and the app keeps showing a conflict banner over a clean tree.

**`repo.Tags["bad name"]` throws.** Even *looking a tag up* validates the ref name, so the
"does it already exist" check has to sit inside the same try/catch as the write —
otherwise an impossible name escapes as a raw `InvalidSpecificationException`.

### Tests

**`TempRepository.Shas()` sorts topologically, not by time.** A test makes all its commits
inside the same second, and git's default ordering leaves those in an arbitrary order.
This showed up as five tests failing on the wrong commit rather than as a flake, which
made it look like a logic bug for far longer than it should have.

**Extend `BranchSwitchingTests` before touching `SwitchBranch`.** Carrying only some files
across a branch switch needs two stashes and six steps, and a mistake there loses work
that was never committed — the one place in this codebase where a bug is unrecoverable.

### Tooling

**Two `.dmg`s in one job collide over the volume name.** `hdiutil create` attaches the
image while it builds it, so building osx-arm64 and then osx-x64 — which the release
job does — has the second one mounting `/Volumes/Omnigit <version>` while the first is
still releasing it. It fails with `Resource busy`, on the second RID only, with the
first `.dmg` already in `dist/`. `build/macos/package.sh` detaches a stale volume and
retries rather than renaming the volume per architecture.

**`Contents/MacOS` is a code location, so only code may live there.** `codesign` treats
everything in it as code and refuses to seal a bundle containing anything it cannot
verify. A plain self-contained publish drops some two hundred managed assemblies in
beside the executable and sealing fails with `code object is not signed at all / In
subcomponent: …/System.Diagnostics.Contracts.dll`, naming whichever one the walk reached
first — which reads like a problem with that assembly and is a problem with the layout.
The macOS build alone publishes with `PublishSingleFile`, which packs the managed
assemblies into the executable and leaves the native libraries beside it.
`IncludeNativeLibrariesForSelfExtract` stays off so those stay visible to be signed.

**Never hand `codesign` the path of a bundle's main executable.** It does not sign that
file: it resolves the path to the enclosing `.app` and signs the whole bundle — early,
without the entitlements, and while the nested binaries are only half done. Every other
Mach-O signs normally, `createdump` included, which is what says it is about the path and
not the file. Skip `Contents/MacOS/$CFBundleExecutable` in the loop; signing the bundle
last is what signs it, and the only way it can carry entitlements.

**A `.p12` made by OpenSSL 3 cannot be imported by `security(1)`.** Its default container
is AES-256-CBC with a PBKDF2/SHA-256 MAC, and `security` reports `MAC verification failed
during PKCS12 import (wrong password?)` — the same message as for a genuinely wrong
password, so the parenthesised guess sends you off changing the wrong secret. Anyone
without a Mac to export from Keychain Access has one of these, `openssl` being how they
had to make it. `build/macos/sign.sh` asks OpenSSL whether the password opens the file
before blaming it, then re-exports with `-keypbe`/`-certpbe PBE-SHA1-3DES -macalg sha1`.

**The hardened runtime kills a .NET app on startup without JIT entitlements.** No dialog,
nothing useful logged. Notarisation requires the hardened runtime, which requires
`com.apple.security.cs.allow-jit` and `allow-unsigned-executable-memory` — the JIT writes
machine code and then executes it, exactly what the runtime exists to stop — plus
`disable-library-validation` for the natives .NET loads through `dlopen`. They live in
`build/macos/entitlements.plist` and go on the bundle only.

**`--deep` is for verifying, never for signing.** Signing with it applies one set of
entitlements to every binary it touches. Sign nested binaries individually, inside out,
because a bundle's signature seals its contents and signing it first invalidates them.

**Notarise the `.app` and the `.dmg` separately.** Stapling only the disk image leaves the
copy dragged into Applications with no ticket of its own, so a first launch on a machine
that is offline is refused. Two round trips per architecture is why the macOS release job
allows 90 minutes. Copy the signed bundle into the image with `ditto`, never `cp` — it is
the copy documented to preserve what a signature and a stapled ticket depend on.

**ImageMagick: `-background none` must come *before* the input file.** After it, the SVG
has already been rasterised onto white and the alpha is gone.

**Every packaging script exits 0 when a tool is missing**, and `gh release upload` has
exited 0 having quietly not uploaded two of the release's assets. That is how 0.2.0 first
shipped with no `.deb` at all. `build/verify-artifacts.sh` exists because nothing short
of asking the published release what it actually contains catches either failure — both
look like a green build.

**`--socket=fallback-x11` is the wrong permission for an Avalonia app.** Avalonia 12 has
no Wayland backend — it draws through XWayland — and `fallback-x11` hands over the X
socket only when the app has *no* Wayland socket. Pair it with `--socket=wayland`, as
almost every Flatpak does, and X11 gets withheld from exactly the desktops that need it:
the app dies at startup with `XOpenDisplay failed`. `--socket=x11`, and nothing else.

**Nothing can `flatpak run` without a session bus, and the error never says so.** A
CI runner has no session D-Bus. Without one, every `flatpak run` dies in bwrap with
`Can't find source path /run/user/1000/doc/by-app/<id>` — the document portal isn't
running to be mounted. `org.flatpak.Builder` fails differently and worse: its entry
point calls `flatpak-spawn --host which flatpak`, which needs the bus, so it silently
finds nothing and reports `Failed to init: Unable to find sdk org.freedesktop.Sdk
version 25.08` — naming a runtime that is installed and present. Both flatpak scripts
re-exec themselves under `dbus-run-session` when `DBUS_SESSION_BUS_ADDRESS` is unset.
Reproduce either locally with `env -u DBUS_SESSION_BUS_ADDRESS`.

**A Flatpak build has no network, so `nuget-sources-*.json` is part of the source.** Bump
a `PackageReference` without regenerating it and every other artifact still builds; the
Flatpak fails partway through a release with a restore error. `check-nuget-sources.sh`
runs in CI to make that a push-time failure instead. See [flatpak.md](flatpak.md).
