namespace GitGui.Models;

/// <summary>
/// A git forge GitGui can talk to. GitHub.com and any number of self-hosted
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

/// <summary>A signed-in identity on a specific <see cref="GitHost"/>.</summary>
public sealed class Account
{
    public required string Login { get; init; }
    public required string DisplayName { get; init; }
    public required GitHost Host { get; init; }
    public required string Initials { get; init; }
    public required string AvatarHex { get; init; }

    /// <summary>How the token was obtained — shown on the accounts screen.</summary>
    public required string AuthMethod { get; init; }

    public int RepositoryCount { get; init; }

    public string Handle => $"@{Login}";
    public string QualifiedName => $"{Login} on {Host.Name}";
}
