<!-- Delete anything that doesn't apply. Small PRs don't need all of this. -->

## What this changes

<!-- What the program does differently afterwards, in a sentence or two. -->

## Why

<!--
Link the issue if there is one. If there isn't, say what problem this solves — a PR
whose reasoning has to be reverse-engineered from the diff is much slower to review.
If you tried an obvious-looking approach first and it didn't work, that's worth a line;
it usually belongs in a comment in the code too.
-->

## How it was tested

- [ ] `dotnet build` passes
- [ ] `dotnet test` passes
- [ ] Ran the app and used the changed part

<!--
Anything touching sign-in, fetch, push or clone should be tried against a real server,
not just a mock — CONTRIBUTING.md has a one-liner for a local Gitea. Say which server
you used.
-->

## Screenshots

<!--
Required for anything visual. Both themes if you touched colours — Tokens.axaml declares
each one per theme variant and it's easy to change only half.
-->

---

- [ ] I've read [CONTRIBUTING.md](CONTRIBUTING.md), and skimmed the gotchas in
      [docs/notes.md](../docs/notes.md) if this touches Avalonia or libgit2
- [ ] New colours go in `Styles/Tokens.axaml`, not inline
- [ ] No token, credential or personal path in the diff or the screenshots
