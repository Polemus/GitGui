namespace Omnigit.Models;

/// <summary>
/// A git forge Omnigit can talk to. GitHub.com and any number of self-hosted
/// Gitea/GitHub Enterprise instances are all modelled the same way.
/// </summary>
public sealed class GitHost
{
    public required string Id { get; init; }

    /// <summary>Display name, e.g. "GitHub" or "git.homelab.net".</summary>
    public required string Name { get; init; }

    public required HostKind Kind { get; init; }

    /// <summary>API/web root, e.g. "https://github.com".</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Brand accent used for badges and chips.</summary>
    public required string AccentHex { get; init; }

    /// <summary>Single character shown in the square host badge.</summary>
    public required string Badge { get; init; }

    public bool IsSelfHosted => Kind == HostKind.Gitea || !BaseUrl.Contains("github.com");

    public string KindLabel => Kind switch
    {
        HostKind.GitHub => "GitHub",
        HostKind.Gitea => "Gitea",
        _ => "Git",
    };
}
