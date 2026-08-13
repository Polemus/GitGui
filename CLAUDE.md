# GitGui — working notes

A desktop git client that talks to more than one hosting site: GitHub and Gitea today,
anything else via a JSON manifest. Avalonia 12 + .NET 10, one codebase for Linux,
Windows and macOS. Private repo: `github.com/Polemus/GitGui`.

## Build and run

```bash
dotnet build                                    # solution is GitGui.slnx, not .sln
dotnet run --project src/GitGui/GitGui.csproj
```

F5 in VS Code works via the checked-in `.vscode/launch.json`.

## Where things live

```
src/GitGui/
  Models/          Plain domain types. No behaviour beyond computed display strings.
  Services/        Git, storage, credentials, activity log.
  HostProviders/   Talking to GitHub/Gitea/etc. See docs/host-manifests.md.
  ViewModels/      MainWindowViewModel drives everything; small per-item VMs beside it.
  Views/           MainWindow, RepositoryView, DiffView, AccountsView.
  Styles/          Tokens.axaml = all colours + icons. Controls.axaml = control styles.
```

Two files matter more than the rest:

- **`Styles/Tokens.axaml`** — every colour, declared once per theme variant. Restyling
  the app means editing only this.
- **`Services/MockData.cs`** — design-time sample content only. Nothing at runtime reads it.

## Decisions worth not re-litigating

**Avalonia, not Tauri/Electron.** The user chose it. Renders with Skia, so it looks
identical on all three platforms.

**Hosting sites are JSON manifests, not plugin DLLs.** A provider handles tokens that can
read and write all of the user's source code. A compiled plugin loaded in-process could
read every token and exfiltrate them, with no practical way to sandbox it. A manifest is
data and cannot execute anything. GitHub is C# only because its browser device login is a
multi-step conversation that can't be expressed as endpoint descriptions. Gitea and GitLab
ship *as manifests* deliberately, so the format is exercised by our own code.

**Expected conditions return values; faults throw.** Being signed out is ordinary for a
git client, so network operations return `SyncResult`. Modelling it as an exception both
muddled control flow and made the debugger halt on every occurrence.

**Tokens never touch a settings file.** `AccountStore` splits an account: harmless half in
`accounts.json`, token in `ICredentialStore` (keyring / Keychain / DPAPI, with a `0600`
file fallback that reports `IsSecure = false` so the UI can warn).

**libgit2 handles are opened per call, not cached.** They aren't thread-safe and the UI
calls in from pooled threads. Opening per call is cheap next to the work each one does.

## Gotchas already paid for

Each of these cost real time. Don't rediscover them.

**Never hand-write `InitializeComponent`.** Avalonia's source generator emits one that
also assigns `x:Name` fields. Writing your own shadows it, the fields stay null, and you
get a `NullReferenceException` in the constructor with no obvious cause.

**`Button` raises `Click` *before* invoking `Command`.** Hiding a flyout in a `Click`
handler tears down the popup's visual tree, which detaches the row's DataContext and makes
`CommandParameter` re-evaluate to **null** before the command runs. Defer the hide with
`Dispatcher.UIThread.Post`.

**Don't throw from inside a libgit2 callback.** The exception has to travel back out
through native frames; the debugger reports it as unhandled in user code and breaks every
time. Record what happened and decide once back in managed code.

**Two theming systems must be kept in step.** Our own styles read `AccentBrush` from
`Tokens.axaml`; stock controls (CheckBox, Slider, focus rings) read `SystemAccentColor*`
from FluentAvalonia. `App.Initialize()` pushes the `BrandAccentColor` token into
`FluentAvaloniaTheme.CustomAccentColor` so one value drives both. Changing the accent means
editing `BrandAccentColor` only.

**`x:Name` on a `Flyout` generates no field** — it isn't a Control. Name the button and use
`TheButton.Flyout?.Hide()`.

**Anything bound with compiled bindings needs `x:DataType` in scope.** A binding inside a
control whose DataContext you reassigned resolves against the *new* type — put visibility
tests on a wrapper element that still has the view model as its DataContext.

**`ActivityLog` writes must be marshalled to the UI thread.** libgit2 progress callbacks
fire on whichever thread is transferring.

**ImageMagick: `-background none` must come *before* the input file.** After it, the SVG
has already been rasterised onto white and the alpha is gone.

**Avalonia 12 renames:** `TextBox.Watermark` → `PlaceholderText`;
`Avalonia.Diagnostics` → `AvaloniaUI.DiagnosticsSupport`.

## Verifying changes

There is no test project yet. Verification so far has been manual, and where it mattered,
against a real server rather than a mock:

```bash
# a real Gitea to test host providers, sign-in, fetch and push against
docker run -d --name gitea-test -p 3333:3000 \
  -e GITEA__security__INSTALL_LOCK=true -e GITEA__database__DB_TYPE=sqlite3 \
  -e GITEA__server__ROOT_URL=http://localhost:3333/ gitea/gitea:1
docker exec -u git gitea-test gitea admin user create \
  --username tester --password 'Test-Pass-123!' --email t@example.com --admin
# token needs write:user AND write:repository to create repos
```

The user runs the app themselves in VS Code. Don't launch it for them unless asked.

## State of play

Working: local git end to end (status, diffs, history, commits), multi-site repository
grouping from real remote URLs, sign-in with tokens, fetch/push/pull, keychain storage,
activity console.

Next, roughly in order:

1. **Binding errors in the debug console** — `SelectedCommit.*` and `PendingDeviceLogin.*`
   bind while null. Harmless but noisy; fix by moving those blocks into a `ContentControl`
   with a `DataTemplate` so the template isn't realised until the value exists.
2. **Browse and clone remote repositories.** Providers already list them
   (`ListRepositoriesAsync`); no UI yet.
3. **A test project.** `UnifiedDiffParser` and the manifest `FieldRef` mapping are pure
   functions over recorded JSON — cheap to cover and easy to break silently.
4. **Git credential-helper fallback**, so people who already use git don't have to sign in
   again. Roughly: shell out to `git credential fill` when no account matches.
5. **Pull requests and issues** — deliberately out of the manifest format for now, since
   their shapes diverge much more between sites.
6. **Refresh the screenshots.** The ones in `docs/screenshots/` predate the activity
   console. The accounts one was deleted rather than left misleading — that screen was
   rebuilt around real sign-in and the old image showed sample accounts that no longer
   exist.

Blocked on the user: **GitHub browser sign-in needs an OAuth App client ID**
(`GITGUI_GITHUB_CLIENT_ID`). It's public, not a secret. Personal access tokens work today
without it.

Unverified: **macOS and Windows credential backends**. Written against the standard tools
(`security`, DPAPI) but never executed — no machines to try them on.
