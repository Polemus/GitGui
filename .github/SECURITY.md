# Security policy

GitGui holds credentials that can read and write all of your source code. Reports about
anything in that blast radius are taken seriously.

## Reporting a vulnerability

**Please don't open a public issue.** Use GitHub's private reporting form:

**[Report a vulnerability →](https://github.com/Polemus/GitGui/security/advisories/new)**

That opens a private thread visible only to you and the maintainer. It's the only
supported channel — there's no security mailing address.

Expect an acknowledgement within a few days. This is a single-maintainer project with no
paid security response, so please read that as a best effort rather than a commitment. If
a report goes unanswered for two weeks, it's fine to chase it by opening a public issue
that says only *"I have sent a private security report"* with no details.

There's no bug bounty.

## What's in scope

The parts of GitGui that would actually cost you something if they were wrong:

- **Credential storage** — `ICredentialStore` and its keyring, Keychain and DPAPI
  backends, and the `0600` file fallback used when none of those are available.
- **The split in `AccountStore`** — anything that would put a token into `accounts.json`,
  which is a plain file, rather than into the credential store.
- **Sign-in flows** — GitHub's OAuth device flow, and personal access token handling for
  every other site.
- **Host manifests** — a manifest is data by design and must not be able to execute
  anything, exfiltrate a token to a host it doesn't belong to, or be used to make GitGui
  send one account's credentials to another site's server.
- **Anything that sends a token somewhere other than the host it was issued for**, or
  writes one to a log, the activity console, or a crash dump.
- **Command injection through repository content** — branch names, remote URLs, tags,
  paths — reaching a shell or a native call.

## What's out of scope

These are known and documented, not findings:

- **Release binaries are unsigned.** macOS Gatekeeper and Windows SmartScreen will both
  warn on first launch. Signing needs an Apple Developer account and a code-signing
  certificate; neither exists yet. Verify downloads by their source, which is the
  Releases page on this repository and nowhere else.
- **The GitHub OAuth client id is checked in.** `DefaultClientId` in `GitHubProvider` is
  GitGui's own OAuth App on github.com. A client id names an application on the approval
  screen and authorises nothing by itself; the device flow is a public-client grant with
  no client secret, so there is no second half to protect. It is gated to github.com —
  an Enterprise server has never heard of it, so those still need
  `GITGUI_GITHUB_CLIENT_ID`, which overrides the default everywhere.
- **The `0600` file fallback for credentials.** When no OS keychain is available, tokens
  land in a file readable only by your user. This is weaker than a keychain on purpose,
  and GitGui reports it — the store sets `IsSecure = false` and the UI warns. A report
  that the fallback is weaker than a keychain isn't a vulnerability; a report that the
  UI *doesn't warn* in some case is.
- **Attacks needing an already-compromised machine.** Anything with your user account can
  already read what your user account can read.
- **Vulnerabilities in dependencies** with no path through GitGui to exploit them.
  Report those upstream. If there is a path through GitGui, report it here.

## Supported versions

Only the latest release. GitGui is pre-1.0 and there are no maintenance branches — fixes
ship in the next version.
