# How GitGui is put together

A short tour of the layers and, more usefully, why they're shaped this way.

## The layers

```
Views (.axaml)          What things look like. No logic beyond visibility.
    ↓ compiled bindings
ViewModels              MainWindowViewModel drives the shell. Async, off the UI thread.
    ↓ interfaces
Services                IGitService, ICredentialStore, IAccountStore, IActivityLog,
                        IRepositoryWatcher, ISystemShell, IFolderPicker
HostProviders           IHostProvider — GitHub in C#, everything else from manifests
    ↓
libgit2 (native)        bundled; no git installation required
```

Views never touch a service. View models never touch libgit2 or HTTP directly.

## Why the git layer looks like this

**Handles are opened per call.** `GitService` creates and disposes a `Repository` inside
every method rather than caching one. libgit2 handles aren't thread-safe and the UI calls
in from pooled threads; opening per call costs far less than the work each call does, and
removes any need for locking.

**Expensive things load on demand.** Commit history loads metadata for 100 commits, but
each commit's diffs load only when it's selected. Diffing 100 commits up front to show a
list is unusably slow.

**Untracked files get a synthetic diff.** libgit2 produces no patch for a file it has
never seen, so `GitService` reads the file and builds an all-added diff, which makes new
files read like any other addition. Binary and oversized files are detected and skipped
rather than dumped into the view as noise.

**Expected outcomes return; faults throw.** `Fetch`/`Pull`/`Push` return `SyncResult`.
Being signed out is an everyday condition for a git client — treating it as an exception
muddles control flow and makes the debugger halt on it during every development run.

**Partial stashing is built out of two whole ones.** Switching branches can carry only
some uncommitted files across, but libgit2 has no way to stash a subset. `SwitchBranch`
gets there by stashing everything, restoring it, reverting the files being carried,
stashing the remainder, restoring the full stash, reverting the files being left, and
dropping the full stash — after which a plain checkout moves what's left in the tree.

The ordering matters more than the step count: **everything is stashed before anything is
reverted**, so uncommitted work never exists only in the working tree. If any later step
fails, the changes are still on the stash stack rather than gone. This is the one place in
the codebase where a bug destroys work that was never committed, which is why it is
covered by tests against real repositories instead of by reasoning.

**A carried file that also differs on the target branch is refused, not attempted.** git
only carries uncommitted work across when the file is identical on both sides; when it
isn't, libgit2 raises `CheckoutConflictException`, which travels back through native
frames and halts the debugger on what is really a question for the user. `SwitchBranch`
therefore compares blob ids between HEAD and the target branch *before* touching anything
and returns a `SwitchResult` naming the files, leaving the working tree untouched so
"leave it behind" is still available. Same rule as `SyncResult`: expected conditions
return values.

**Acting on an old commit refuses a dirty working tree.** Reverting and cherry-picking
both stop part-way when git can't merge something, leaving conflict markers in the files.
If there were already uncommitted changes in those files, nothing afterwards could tell
the two apart. `SwitchBranch` earns its stash dance because switching branches mid-edit is
routine; reverting mid-edit is not, so it is refused with a message instead.

**A cherry-pick switches to the target branch first, and stays there.** git applies a
cherry-pick to whatever HEAD is on — "apply that commit to that other branch from here"
does not exist. If it conflicts, the half-applied state is on the target branch, which is
exactly where the user needs to be to finish it.

**A stopped operation is read from the index, not the working tree.** A conflict is up to
three index entries for one path — ancestor, ours, theirs — and the file may not exist in
the tree at all if one side deleted it. `GetConflictedPaths` therefore reads
`repo.Index.Conflicts`; resolving writes the chosen blob out and stages it, which is what
collapses the three entries back into one.

**Finishing cleans up after itself; abandoning has to be done by hand.** libgit2 has
`git_repository_state_cleanup`, but LibGit2Sharp doesn't expose it. `Repository.Commit`
calls it internally, so committing a resolved revert clears the state. `AbortOperation`
gets no such help and deletes `MERGE_HEAD`, `REVERT_HEAD`, `CHERRY_PICK_HEAD`, `MERGE_MSG`,
`MERGE_MODE` and `sequencer/` itself. Miss one and the app keeps showing a conflict banner
over a clean tree.

## Why hosting sites are data, not code

A host provider handles tokens that can read and write all of the user's source code.

- A **plugin DLL** loaded into the process could read the tokens held for *every* connected
  site and send them anywhere. .NET offers no practical sandbox for this.
- A **JSON manifest** cannot execute anything. The worst a hostile one can do is point at
  the wrong server — which is visible in the file.

That asymmetry decided it. The cost is real and accepted: a site whose sign-in is a genuine
multi-step conversation can't be described as data. GitHub's browser device login is
exactly that, so GitHub is C#. Everything else about GitHub could have been a manifest.

Gitea and GitLab ship *as manifests* on purpose. If the built-ins were code and only users
wrote manifests, the format would quietly rot into something that works in theory.

See [host-manifests.md](host-manifests.md) for the format.

## Two things that group repositories, and why both exist

- **`HostResolver`** parses a clone's `origin` URL to decide which site it belongs to. It
  works offline with no network round-trip, which is what listing local repositories needs.
  It assumes anything that isn't `github.com` is Gitea.
- **`IHostProvider.RecognisesAsync`** probes a URL (`/api/v1/version`) and answers
  definitively. It costs a network call, so it runs when connecting an account.

The cheap guess is used where speed matters; the accurate probe where correctness does.

## Theming

Every colour is a token in `Styles/Tokens.axaml`, declared once per theme variant and
referenced with `DynamicResource`, so switching light/dark repaints without a restart.

There are **two** theming systems and they must agree:

| Consumer | Reads from |
| --- | --- |
| our own styles | `AccentBrush` in `Tokens.axaml` |
| stock controls (CheckBox, Slider, focus rings) | `SystemAccentColor*`, generated by FluentAvalonia |

`App.Initialize()` reads the `BrandAccentColor` token and pushes it into
`FluentAvaloniaTheme.CustomAccentColor`, so one value feeds both. Hardcoding the colour in
`App.axaml` would create a second copy that silently drifts.

Light theme deepens the accent (`#2B87B5` vs `#3399CC`) so white button text clears WCAG AA.
That lands within 8/255 of the shade FluentAvalonia independently derives for light theme,
so stock and custom controls match. Using the raw brand colour there would make them differ
*visibly*.

## Credentials

`AccountStore` splits an account in two:

- `accounts.json` — provider id, base URL, login, display name. Nothing sensitive.
- `ICredentialStore` — the token, in the OS keyring.

An account whose token has vanished is dropped on load rather than presented as
half-working. The file fallback reports `IsSecure = false`, and the accounts screen says so
plainly rather than implying safety it doesn't have.

## Threading

Git work runs via `Task.Run`; the `await` resumes on the UI thread, so collection updates
after it are already marshalled. `ActivityLog` marshals explicitly because libgit2 progress
callbacks fire on whichever thread is transferring, not a thread we chose.
`RepositoryWatcher` does the same, for the same reason: `FileSystemWatcher` events arrive
on a thread-pool thread.

## Staying in step with the disk

The app used to notice only its own work, so committing in a terminal left it offering to
commit files that no longer differed. `RepositoryWatcher` watches the working tree and
`.git` together, debounced, ignoring the paths that churn without meaning anything —
`.git/objects`, reflogs, lock files, build output.

An automatic reload is deliberately quieter than a deliberate one: no busy strip, no log
line, and the ticked files, selected file and selected commit are all preserved. The user
didn't ask for it, so it shouldn't move anything under them.

## What still gets checked by hand

`dotnet test` covers the pure functions and everything that touches a repository. It does
not cover the network or the UI, and a mock server would only prove we agree with
ourselves — so host providers, sign-in, fetch and push are tried against a real Gitea:

```bash
docker run -d --name gitea-test -p 3333:3000 \
  -e GITEA__security__INSTALL_LOCK=true -e GITEA__database__DB_TYPE=sqlite3 \
  -e GITEA__server__ROOT_URL=http://localhost:3333/ gitea/gitea:1

docker exec -u git gitea-test gitea admin user create \
  --username tester --password 'Test-Pass-123!' --email t@example.com --admin
```

Create a token in that instance's UI with **write:user** and **write:repository** — both
are needed to create repositories — and sign in to `http://localhost:3333` from the
Accounts screen.

The macOS and Windows credential backends are the other gap. They are written against the
standard tools (`security`, DPAPI) but have never been executed, for want of machines to
try them on.
