using GitGui.Services;

namespace GitGui.Tests;

/// <summary>
/// Building the "View on …" link. Pure string work, but it is the one place a clone's
/// remote URL has to line up with what a hosting site calls the same repository.
/// </summary>
public class WebLinkTests
{
    private const string Sha = "1e693031f0a1b2c3d4e5f60718293a4b5c6d7e8f";

    [Fact]
    public void TheUsualShapeIsBuiltFromTheRemoteAlone()
    {
        var url = WebLinks.CommitUrl("https://github.com/Polemus/GitGui.git", Sha);

        Assert.Equal($"https://github.com/Polemus/GitGui/commit/{Sha}", url?.ToString());
    }

    [Fact]
    public void AnSshRemoteGivesTheSameLinkAsAnHttpsOne()
    {
        var https = WebLinks.CommitUrl("https://github.com/Polemus/GitGui.git", Sha);
        var ssh = WebLinks.CommitUrl("git@github.com:Polemus/GitGui.git", Sha);

        Assert.Equal(https, ssh);
    }

    [Fact]
    public void ASiteWithItsOwnShapeUsesItsTemplate()
    {
        var url = WebLinks.CommitUrl(
            new Uri("https://gitlab.com"), "group/sub", "thing", Sha,
            "{base}/{owner}/{repo}/-/commit/{sha}");

        Assert.Equal($"https://gitlab.com/group/sub/thing/-/commit/{Sha}", url?.ToString());
    }

    [Fact]
    public void APathPrefixOnTheSiteIsKept()
    {
        // A Gitea served from example.com/git must not lose the /git.
        var url = WebLinks.CommitUrl(new Uri("https://example.com/git/"), "me", "thing", Sha);

        Assert.Equal($"https://example.com/git/me/thing/commit/{Sha}", url?.ToString());
    }

    [Fact]
    public void ARemoteWithNoOwnerDoesNotLeaveAnEmptySegment()
    {
        var url = WebLinks.CommitUrl(new Uri("https://git.example.com"), string.Empty, "thing", Sha);

        Assert.Equal($"https://git.example.com/thing/commit/{Sha}", url?.ToString());
    }

    [Fact]
    public void ARemoteThatNamesNoSiteHasNoLink()
    {
        Assert.Null(WebLinks.CommitUrl("/home/me/scratch-repo", Sha));
        Assert.Null(WebLinks.CommitUrl((string?)null, Sha));
    }

    [Fact]
    public void ATemplateThatDoesNotMakeAUrlIsNoLinkRatherThanABadOne()
    {
        var url = WebLinks.CommitUrl(
            new Uri("https://example.com"), "me", "thing", Sha, "commits/{sha}");

        Assert.Null(url);
    }
}
