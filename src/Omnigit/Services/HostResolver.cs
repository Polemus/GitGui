using System;
using System.Collections.Concurrent;
using Omnigit.Models;

namespace Omnigit.Services;

/// <summary>A remote URL broken into the parts the UI cares about.</summary>
public sealed record RemoteIdentity(GitHost Host, string Owner, string Name);

/// <summary>
/// Works out which forge a clone belongs to by parsing its origin URL. This is what
/// makes the multi-forge story real rather than cosmetic - a repo is grouped under
/// GitHub or under a self-hosted instance purely from where it actually came from.
/// </summary>
public static class HostResolver
{
    // One GitHost instance per domain, so repos from the same host group together
    // by reference in the picker.
    private static readonly ConcurrentDictionary<string, GitHost> Hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stand-in for clones with no usable remote (purely local repos).</summary>
    public static GitHost LocalOnly { get; } = new()
    {
        Id = "local",
        Name = "Local only",
        Kind = HostKind.Gitea,
        BaseUrl = string.Empty,
        AccentHex = "#6B7385",
        Badge = "L",
    };

    /// <summary>
    /// Parses an origin URL. Handles the three shapes git remotes come in:
    /// <c>https://host/owner/repo.git</c>, <c>ssh://git@host/owner/repo.git</c> and
    /// the scp-like <c>git@host:owner/repo.git</c>.
    /// </summary>
    public static RemoteIdentity? Parse(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return null;

        var url = remoteUrl.Trim();
        string domain;
        string path;

        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            domain = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            // scp-like syntax: [user@]host:path
            var at = url.IndexOf('@');
            var colon = url.IndexOf(':', Math.Max(at, 0) + 1);
            if (colon < 0)
                return null;

            domain = url[(at + 1)..colon];
            path = url[(colon + 1)..];
        }

        if (string.IsNullOrEmpty(domain))
            return null;

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        var owner = slash >= 0 ? path[..slash] : string.Empty;

        if (string.IsNullOrEmpty(name))
            return null;

        return new RemoteIdentity(ForDomain(domain), owner, name);
    }

    /// <summary>Returns the shared <see cref="GitHost"/> for a domain, creating it once.</summary>
    public static GitHost ForDomain(string domain) =>
        Hosts.GetOrAdd(domain, d =>
        {
            var isGitHub = d.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                        || d.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase);

            return new GitHost
            {
                Id = d.ToLowerInvariant(),
                Name = isGitHub ? "GitHub" : d,
                // Anything that isn't github.com is assumed to be Gitea for now.
                // Phase 2 confirms it by probing /api/v1/version before we rely on
                // the API dialect for anything.
                Kind = isGitHub ? HostKind.GitHub : HostKind.Gitea,
                BaseUrl = $"https://{d}",
                AccentHex = isGitHub ? "#3399CC" : "#609926",
                Badge = isGitHub ? "G" : char.ToUpperInvariant(d[0]).ToString(),
            };
        });
}
