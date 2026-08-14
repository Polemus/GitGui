using Omnigit.Models;
using Omnigit.Services;

namespace Omnigit.Tests;

/// <summary>
/// Deciding which hosting site a clone belongs to from its origin URL. This is what
/// makes the repository picker group a GitHub clone apart from a self-hosted one, so
/// every remote spelling git accepts has to survive it.
/// </summary>
public class HostResolverTests
{
    [Theory]
    [InlineData("https://github.com/Polemus/Omnigit.git")]
    [InlineData("https://github.com/Polemus/Omnigit")]
    [InlineData("ssh://git@github.com/Polemus/Omnigit.git")]
    [InlineData("git@github.com:Polemus/Omnigit.git")]
    [InlineData("github.com:Polemus/Omnigit.git")]
    public void EverySpellingOfTheSameRemoteAgrees(string url)
    {
        var identity = HostResolver.Parse(url);

        Assert.NotNull(identity);
        Assert.Equal("Polemus", identity.Owner);
        Assert.Equal("Omnigit", identity.Name);
        Assert.Equal(HostKind.GitHub, identity.Host.Kind);
    }

    [Fact]
    public void TheDotGitSuffixIsOptionalAndCaseInsensitive()
    {
        Assert.Equal("Omnigit", HostResolver.Parse("https://example.com/o/Omnigit.GIT")!.Name);
    }

    [Fact]
    public void ASelfHostedDomainIsItsOwnHost()
    {
        var identity = HostResolver.Parse("https://git.example.com/team/thing.git");

        Assert.NotNull(identity);
        Assert.Equal("team", identity.Owner);
        Assert.Equal("thing", identity.Name);
        Assert.NotEqual(HostKind.GitHub, identity.Host.Kind);
    }

    [Fact]
    public void NestedGroupsKeepTheirFullPathAsTheOwner()
    {
        // GitLab subgroups are the reason owner is not just the first segment.
        var identity = HostResolver.Parse("https://gitlab.com/group/subgroup/project.git");

        Assert.Equal("group/subgroup", identity!.Owner);
        Assert.Equal("project", identity.Name);
    }

    [Fact]
    public void APortIsNotMistakenForThePath()
    {
        var identity = HostResolver.Parse("https://git.example.com:3333/tester/repo.git");

        Assert.Equal("git.example.com", identity!.Host.Name);
        Assert.Equal("tester", identity.Owner);
        Assert.Equal("repo", identity.Name);
    }

    [Fact]
    public void ARepositoryWithNoOwnerStillResolves()
    {
        var identity = HostResolver.Parse("git@example.com:thing.git");

        Assert.Equal(string.Empty, identity!.Owner);
        Assert.Equal("thing", identity.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("https://")]
    [InlineData("https://example.com/")]
    public void UnusableRemotesGiveNullRatherThanAGuess(string? url)
    {
        Assert.Null(HostResolver.Parse(url));
    }

    [Fact]
    public void SurroundingWhitespaceIsIgnored()
    {
        Assert.Equal("Omnigit", HostResolver.Parse("  https://github.com/Polemus/Omnigit.git  ")!.Name);
    }

    [Fact]
    public void LocalOnlyIsAvailableForClonesWithNoRemote()
    {
        Assert.Equal("Local only", HostResolver.LocalOnly.Name);
    }
}
