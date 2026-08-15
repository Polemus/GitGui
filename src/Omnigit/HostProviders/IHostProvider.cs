using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Omnigit.HostProviders;

/// <summary>
/// Knows how to talk to one kind of git hosting site. Implemented either by real C#
/// (for sites whose login is too custom to describe as data, like GitHub's browser
/// flow) or by <see cref="ManifestHostProvider"/> reading a JSON description.
/// </summary>
/// <remarks>
/// Anything added here has to be expressible in a manifest, or manifest-defined sites
/// silently lose the feature. Keeping this interface small is what keeps user-authored
/// sites first-class rather than second-tier.
/// </remarks>
public interface IHostProvider
{
    /// <summary>Stable identifier, e.g. "github" or "gitea". Used as a storage key.</summary>
    string Id { get; }

    string DisplayName { get; }

    HostCapabilities Capabilities { get; }

    /// <summary>
    /// True if the site at this URL is one this provider handles. Replaces guessing
    /// from the domain name - a self-hosted instance can be on any domain.
    /// </summary>
    Task<bool> RecognisesAsync(Uri baseUrl, CancellationToken cancellationToken);

    /// <summary>Signs in with a token the user created on the site themselves.</summary>
    Task<HostAccount> SignInWithTokenAsync(Uri baseUrl, string token, CancellationToken cancellationToken);

    /// <summary>
    /// Begins a browser login, returning the code to show the user. Throws
    /// <see cref="NotSupportedException"/> unless
    /// <see cref="AuthMethod.BrowserDeviceLogin"/> is in <see cref="Capabilities"/>.
    /// </summary>
    Task<DeviceLogin> StartBrowserLoginAsync(Uri baseUrl, CancellationToken cancellationToken);

    /// <summary>Waits for the user to approve a browser login, then returns the account.</summary>
    Task<HostAccount> CompleteBrowserLoginAsync(Uri baseUrl, DeviceLogin login, CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteRepository>> ListRepositoriesAsync(HostAccount account, CancellationToken cancellationToken);

    /// <summary>
    /// Open pull requests for one repository, newest first. Empty when the provider
    /// can't list them - see <see cref="HostCapabilities.CanListPullRequests"/>.
    /// </summary>
    Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(
        HostAccount account, string owner, string repository, CancellationToken cancellationToken);

    /// <summary>
    /// The username/password libgit2 should use for HTTPS fetch and push. Sites differ:
    /// GitHub wants the token as the password, GitLab wants a fixed username.
    /// </summary>
    GitCredentials GetGitCredentials(HostAccount account);

    /// <summary>
    /// Where a commit is shown on the site's website, as a template over
    /// <c>{base}</c>, <c>{owner}</c>, <c>{repo}</c> and <c>{sha}</c>. A string rather
    /// than a method so a manifest can supply it - GitLab's URLs differ from everyone
    /// else's and that has to be describable without writing code.
    /// </summary>
    string CommitUrlTemplate { get; }

    /// <summary>
    /// The site's "open a pull request" page, as a template over <c>{base}</c>,
    /// <c>{owner}</c>, <c>{repo}</c>, <c>{source}</c> and <c>{target}</c>. Creating one
    /// is deliberately a hand-off to the browser rather than an API call: the form asks
    /// for reviewers, labels, templates and a dozen other things that differ per site,
    /// and GitHub Desktop hands off for the same reason.
    /// </summary>
    string NewPullRequestUrlTemplate { get; }

    /// <summary>
    /// The ref on the origin remote holding a pull request's head, over
    /// <c>{number}</c> - <c>refs/pull/{number}/head</c> nearly everywhere, but GitLab
    /// files merge requests somewhere else entirely. This is what makes checking one
    /// out possible without adding the contributor's fork as a remote.
    /// </summary>
    string PullRequestRefSpec { get; }
}
