# Contributing to GitGui

Thanks for looking. GitGui is a small project with one maintainer, so the most useful
thing you can do before writing code is open an issue and say what you have in mind —
it's cheaper for both of us than a pull request that turns out to duplicate work or cut
across a decision already made.

## Getting it building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Nothing else — not
even git, since LibGit2Sharp bundles its own native library.

```bash
git clone https://github.com/Polemus/GitGui.git
cd GitGui
dotnet build                                    # the solution is GitGui.slnx, not .sln
dotnet run --project src/GitGui/GitGui.csproj
```

VS Code users get F5 from the checked-in `.vscode/launch.json`.

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

[`docs/architecture.md`](../docs/architecture.md) explains why the layers are shaped this
way. [`docs/notes.md`](../docs/notes.md) is the other half: decisions that look arbitrary
from outside and aren't, plus a list of Avalonia and libgit2 traps that each cost real
time to find. **Read the gotchas before debugging anything odd** — there's a fair chance
yours is already in there.

## Tests

```bash
dotnet test
```

These cover the pure functions (`UnifiedDiffParser`, `HostResolver.Parse`, `WebLinks`,
manifest field mapping), the host connection path against a stub `HttpMessageHandler`,
and everything that touches a repository, which runs against throwaway repositories built
by `TempRepository`. No display, no network and no installed git required, so they work
anywhere CI does.

**If you touch `SwitchBranch`, extend `BranchSwitchingTests` first.** Carrying only some
files across a branch switch takes two stashes and six steps, and a mistake there loses
work that was never committed. It is the one place in this codebase where a bug is
unrecoverable.

Anything touching sign-in, fetch or push should be tried against a real server rather
than a mock. A local Gitea is a one-liner:

```bash
docker run -d --name gitea-test -p 3333:3000 \
  -e GITEA__security__INSTALL_LOCK=true -e GITEA__database__DB_TYPE=sqlite3 \
  -e GITEA__server__ROOT_URL=http://localhost:3333/ gitea/gitea:1
docker exec -u git gitea-test gitea admin user create \
  --username tester --password 'Test-Pass-123!' --email t@example.com --admin
# the token needs write:user AND write:repository to create repositories
```

## Style

`.editorconfig` carries the formatting rules and your editor should apply them — four
spaces in C#, two in XAML and JSON, LF endings, file-scoped namespaces. Beyond that:
write code that reads like the code around it.

Comments in this codebase explain **why**, not what. A comment restating the line below
it will get removed; one recording why an obvious-looking approach doesn't work is worth
more than the code it sits on.

Commit messages are a plain sentence in the imperative, describing what the commit
changes about the program — `Fail a release that ships without all of its artifacts`,
not `fix(ci): add verification`. No prefixes, no ticket numbers, no tags.

## Pull requests

- Branch from `main`, and keep one PR to one concern.
- Make sure `dotnet build` and `dotnet test` both pass — CI runs them on Linux and will
  tell you either way, but finding out locally is faster.
- Say in the description what you changed and, if it isn't obvious, what you tried that
  didn't work. Screenshots for anything visual, both themes if you touched colours.
- New colours go in `Styles/Tokens.axaml` and nowhere else, once per theme variant.
- Adding support for a new hosting site usually needs **no C# at all** — see
  [`docs/host-manifests.md`](../docs/host-manifests.md). A manifest is data and can't
  execute anything, which is deliberate: providers hold tokens that read and write all of
  a user's source code. A PR that adds a compiled provider instead will be turned down
  unless the site genuinely can't be described as data, as is the case for GitHub's
  multi-step device login.

First-time contributors' workflow runs need maintainer approval before CI will start.
That's a GitHub default, not a comment on your patch.

## What's wanted

The [README's known gaps](../README.md#known-gaps) are the honest backlog. Reordering
commits, a git credential-helper fallback, and history paging are all real work that
nobody is currently doing.

## Reporting bugs and vulnerabilities

Ordinary bugs: [open an issue](https://github.com/Polemus/GitGui/issues/new/choose).

Security problems — anything touching tokens, the credential store, or the sign-in flows
— go through [SECURITY.md](SECURITY.md) instead. Please don't file those as public
issues.
