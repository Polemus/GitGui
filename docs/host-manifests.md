# Adding a git hosting site

Omnigit talks to more than one kind of hosting site. Adding a new one needs **no code** —
just a small JSON file describing the site's API.

You have two ways to do it. **Settings → Hosting sites → Add a site** fills in the same
fields through a form and writes the file for you; the rest of this document describes the
file it writes, which you can also create by hand. Neither route is privileged — a site
added through the UI is an ordinary JSON file you can edit, copy or delete afterwards.

Put your file here:

| Platform | Folder |
| --- | --- |
| Linux | `~/.config/Omnigit/hosts/` |
| Windows | `%APPDATA%\Omnigit\hosts\` |
| macOS | `~/Library/Application Support/Omnigit/hosts/` |

Any `.json` file in that folder is loaded at startup. A file whose `id` matches one that
ships with Omnigit **replaces** it, so you can always fix a built-in description yourself
without waiting for a release.

## Why JSON and not a plugin DLL

A hosting site provider handles your access tokens — tokens that can read and write all
your source code. A compiled plugin loaded into the app could read the tokens for *every*
site you've connected and quietly send them somewhere, and there's no practical way to
sandbox it.

A manifest is **data, not a program**. It can't execute anything. The worst a malicious
manifest can do is point at the wrong server, which is visible in the file itself. That
tradeoff is why this is JSON.

The cost is real, though: a site whose sign-in is a genuine multi-step conversation
can't be described this way. GitHub's browser login is exactly that, which is why GitHub
is built into Omnigit as real code. Everything else about GitHub *could* have been a
manifest.

## A complete example

This is `gitea.json`, which ships with Omnigit. It goes through exactly the same code
path yours will:

```json
{
  "id": "gitea",
  "displayName": "Gitea",

  "recognise": {
    "path": "/api/v1/version",
    "expectField": "version"
  },

  "authHeader": {
    "name": "Authorization",
    "value": "token {token}"
  },

  "endpoints": {
    "currentUser": "/api/v1/user",
    "repositories": "/api/v1/user/repos?limit=100",
    "pullRequests": "/api/v1/repos/{owner}/{repo}/pulls?state=open&sort=recentupdate&limit=50"
  },

  "userFields": {
    "login": "login",
    "displayName": "full_name",
    "avatarUrl": "avatar_url"
  },

  "repositoryFields": {
    "name": "name",
    "owner": "owner.login",
    "cloneUrl": "clone_url",
    "defaultBranch": "default_branch",
    "isPrivate": "private",
    "description": "description",
    "updatedAt": "updated_at"
  },

  "pullRequestFields": {
    "number": "number",
    "title": "title",
    "author": "user.login",
    "sourceBranch": "head.ref",
    "targetBranch": "base.ref",
    "isDraft": "draft",
    "updatedAt": "updated_at",
    "webUrl": "html_url"
  },

  "pullRequestRef": "refs/pull/{number}/head",

  "gitCredentials": {
    "username": "{login}",
    "password": "{token}"
  },

  "webUrls": {
    "commit": "{base}/{owner}/{repo}/commit/{sha}",
    "newPullRequest": "{base}/{owner}/{repo}/compare/{target}...{source}?expand=1"
  }
}
```

## The fields

### `id` and `displayName`

`id` is a short stable key (`"gitea"`, `"gitlab"`). It identifies the site in stored
settings, so changing it later disconnects existing accounts. `displayName` is what the
user sees.

### `recognise`

How Omnigit confirms a URL really is this kind of site. It fetches `path` and checks that
`expectField` exists in the JSON response.

```json
"recognise": { "path": "/api/v1/version", "expectField": "version" }
```

This is what lets a self-hosted instance live on any domain. Omnigit does **not** guess
from the domain name.

If two providers recognise the same server, the first one loaded wins. Give yours a
distinct `recognise` path if that matters.

### `authHeader`

The HTTP header carrying the token. `{token}` is substituted.

```json
"authHeader": { "name": "Authorization", "value": "token {token}" }
"authHeader": { "name": "PRIVATE-TOKEN", "value": "{token}" }
```

### `endpoints`

- `currentUser` — returns the signed-in user. Required; sign-in can't work without it.
- `repositories` — returns an array of repositories. Include any paging or filter query
  string you need.
- `pullRequests` — returns an array of open pull requests for one repository. `{owner}`
  and `{repo}` are substituted, since unlike the other two this endpoint is about a
  particular clone. Leave it out and Omnigit hides the pull request tab for this site
  rather than showing a list that could never fill.

### `userFields` and `repositoryFields`

Where to find each value in the site's JSON. Use a dotted path to reach into nested
objects: `owner.login` reads `{"owner": {"login": "..."}}`.

When a site encodes a boolean as a string, use the object form instead:

```json
"isPrivate": { "path": "visibility", "equals": "private" }
```

GitLab needs exactly that, which is why the shorthand alone isn't enough.

### `pullRequestFields`

Where each part of a pull request sits in that array's entries. The defaults are
GitHub's shape, which Gitea copied. GitLab needs all of them, and note `iid` rather than
`id` — `id` is global across the instance and would name someone else's merge request:

```json
"pullRequestFields": {
  "number": "iid",
  "author": "author.username",
  "sourceBranch": "source_branch",
  "targetBranch": "target_branch",
  "webUrl": "web_url"
}
```

### `pullRequestRef`

The ref on the origin remote holding a pull request's head, over `{number}`. This is
what lets Omnigit check out a pull request opened from someone's fork without adding
that fork as a remote — the head is already on the remote you cloned from.

```json
"pullRequestRef": "refs/pull/{number}/head"           // GitHub, Gitea
"pullRequestRef": "refs/merge-requests/{number}/head" // GitLab
```

The checkout lands on a local branch named `pr/<number>`, never the source branch's own
name: a branch on a fork can be called anything, including something already here.

### `webUrls`

Pages on the site's own website, as opposed to its API. Built from what a clone already
knows rather than fetched, so `{base}`, `{owner}` and `{repo}` are always available.

- `commit` — where "View on …" sends a browser. `{sha}` is substituted.
- `newPullRequest` — where "Create pull request" sends it. `{source}` and `{target}` are
  the branch being proposed and the one it would merge into, both URL-escaped.

Opening a pull request is deliberately a hand-off to the site's own form rather than an
API call: that form asks for reviewers, labels and templates that differ per site, and
GitHub Desktop hands off for the same reason.

### `gitCredentials`

What to hand git for HTTPS fetch and push. `{login}` and `{token}` are substituted.
Sites differ — GitHub takes the token as the password, GitLab wants a fixed username:

```json
"gitCredentials": { "username": "oauth2", "password": "{token}" }
```

## Writing one for a new site

1. Find the site's API docs and its version endpoint.
2. Call `curl -H 'Authorization: ...' https://your-site/api/.../user` and look at the
   JSON. The field names you see are what go in `userFields`.
3. Do the same for the repository list endpoint to fill in `repositoryFields`.
4. Save the file and add an account. A site added through **Settings → Hosting sites** is
   usable straight away; a file you drop in the folder by hand is picked up at startup.

### Test connection

The form has a **Test connection** button. Give it the address of a server running the
site — and, if you have one, a token — and it reports each step separately: whether
`recognise` matched, whether `endpoints.currentUser` returned a login through your
`userFields` mapping, and how many repositories `endpoints.repositories` listed. A wrong
path or field name is named in the result rather than showing up later as a failed
sign-in. Nothing is saved by testing: neither the address nor the token is written to the
manifest, and the token is not stored anywhere.

Without a token only the `recognise` check runs, which is still the one that catches a
wrong base path.

If a manifest is malformed or missing an `id`, Omnigit logs a warning naming the file and
carries on with the rest — one bad file never stops the app starting.

## Known limits

- **Token sign-in only.** Browser-based OAuth needs code, not data.
- **One page of results.** Put a large `per_page`/`limit` in the endpoint query string;
  automatic paging isn't implemented yet.
- **Pull requests are listed, checked out and opened — not merged or reviewed.** There
  is no way to comment, approve or merge from Omnigit, and no check/CI status. Issues
  aren't covered by the format at all.
