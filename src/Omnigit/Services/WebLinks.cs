using System;

namespace Omnigit.Services;

/// <summary>
/// Turns a clone plus a commit into the page a browser can open.
/// </summary>
/// <remarks>
/// A template rather than a code path per site, so a hosting site added from the UI can
/// describe its own URL shape the same way it describes its endpoints. Most forges agree
/// on <c>/owner/repo/commit/sha</c>; GitLab is the reason the shape is configurable at
/// all, since it puts a <c>/-/</c> in the middle.
/// </remarks>
public static class WebLinks
{
    /// <summary>What GitHub, Gitea and most others use.</summary>
    public const string DefaultCommitTemplate = "{base}/{owner}/{repo}/commit/{sha}";

    /// <summary>
    /// The compare page, which is where GitHub and Gitea open a new pull request from.
    /// <c>expand=1</c> opens the form rather than the diff.
    /// </summary>
    public const string DefaultNewPullRequestTemplate =
        "{base}/{owner}/{repo}/compare/{target}...{source}?expand=1";

    /// <summary>Where a pull request's head lives on the origin remote.</summary>
    public const string DefaultPullRequestRefSpec = "refs/pull/{number}/head";

    /// <summary>
    /// Fills in a commit template. Null when the pieces don't make an absolute URL,
    /// which is what a template with a typo in it produces.
    /// </summary>
    /// <param name="baseUrl">
    /// The site root. Any path prefix on it is kept, so a Gitea at example.com/git works.
    /// </param>
    public static Uri? CommitUrl(Uri baseUrl, string owner, string repository, string sha, string? template = null)
    {
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(sha))
            return null;

        var text = string.IsNullOrWhiteSpace(template) ? DefaultCommitTemplate : template;

        // A remote with no owner - a bare path on a plain git server - would otherwise
        // leave a doubled slash where the owner should be.
        if (string.IsNullOrEmpty(owner))
            text = text.Replace("{owner}/", string.Empty, StringComparison.Ordinal);

        text = text
            .Replace("{base}", baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/'), StringComparison.Ordinal)
            .Replace("{owner}", owner, StringComparison.Ordinal)
            .Replace("{repo}", repository, StringComparison.Ordinal)
            .Replace("{sha}", sha, StringComparison.Ordinal);

        return Uri.TryCreate(text, UriKind.Absolute, out var url)
               && url.Scheme is "http" or "https"
            ? url
            : null;
    }

    /// <summary>
    /// The page for opening a pull request from <paramref name="source"/> into
    /// <paramref name="target"/>. Branch names are escaped: a branch called
    /// <c>fix/#12</c> would otherwise cut the URL short at the fragment.
    /// </summary>
    public static Uri? NewPullRequestUrl(
        Uri baseUrl, string owner, string repository, string source, string target, string? template = null)
    {
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(source))
            return null;

        var text = string.IsNullOrWhiteSpace(template) ? DefaultNewPullRequestTemplate : template;

        if (string.IsNullOrEmpty(owner))
            text = text.Replace("{owner}/", string.Empty, StringComparison.Ordinal);

        text = text
            .Replace("{base}", baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/'), StringComparison.Ordinal)
            .Replace("{owner}", owner, StringComparison.Ordinal)
            .Replace("{repo}", repository, StringComparison.Ordinal)
            .Replace("{source}", Uri.EscapeDataString(source), StringComparison.Ordinal)
            .Replace("{target}", Uri.EscapeDataString(target), StringComparison.Ordinal);

        return Uri.TryCreate(text, UriKind.Absolute, out var url)
               && url.Scheme is "http" or "https"
            ? url
            : null;
    }

    /// <summary>
    /// The ref a pull request's head sits at on the remote, e.g.
    /// <c>refs/pull/12/head</c>. Empty when the template names no ref.
    /// </summary>
    public static string PullRequestRef(int number, string? template = null)
        => (string.IsNullOrWhiteSpace(template) ? DefaultPullRequestRefSpec : template)
            .Replace("{number}", number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

    /// <summary>
    /// The same, worked out from a remote URL alone. Used when no account is signed in
    /// for the site, where the domain is all we know - which is still enough for a link.
    /// </summary>
    public static Uri? CommitUrl(string? remoteUrl, string sha, string? template = null)
    {
        if (HostResolver.Parse(remoteUrl) is not { } identity)
            return null;

        return Uri.TryCreate(identity.Host.BaseUrl, UriKind.Absolute, out var baseUrl)
            ? CommitUrl(baseUrl, identity.Owner, identity.Name, sha, template)
            : null;
    }
}
