using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GitGui.HostProviders;

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
    /// The username/password libgit2 should use for HTTPS fetch and push. Sites differ:
    /// GitHub wants the token as the password, GitLab wants a fixed username.
    /// </summary>
    GitCredentials GetGitCredentials(HostAccount account);
}
