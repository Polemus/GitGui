using LibGit2Sharp;
using Omnigit.Services;

namespace Omnigit.Tests;

/// <summary>
/// Which branch this repository's work merges back into.
/// </summary>
/// <remarks>
/// This was the branch currently checked out, which is the opposite of the question:
/// "open a pull request from here into the default" became a proposal to merge a branch
/// into itself, so the button was disabled on every repository and never said why.
/// </remarks>
public class DefaultBranchTests
{
    private static readonly IGitService Git = new GitService();

    private static string DefaultOf(TempRepository repository)
        => Git.OpenRepository(repository.Path).DefaultBranch;

    /// <summary>Checks out a new branch and leaves the repository on it.</summary>
    private static void WorkOn(TempRepository repository, string name)
    {
        using var repo = new Repository(repository.Path);
        Commands.Checkout(repo, repo.CreateBranch(name));
    }

    [Fact]
    public void TheRemotesOwnHeadIsTheAnswerWhenItIsRecorded()
    {
        using var repo = new TempRepository();

        repo.Write("file.txt", "one\n");
        repo.Commit("first");
        repo.AddOrigin();
        repo.PushWithoutUpstream();

        var trunk = repo.CurrentBranch();

        using (var raw = new Repository(repo.Path))
            raw.Refs.Add($"refs/remotes/origin/HEAD", $"refs/remotes/origin/{trunk}");

        WorkOn(repo, "a-feature");

        Assert.Equal(trunk, DefaultOf(repo));
    }

    /// <summary>
    /// A repository that was init'ed and pushed has no <c>refs/remotes/origin/HEAD</c> -
    /// the same gap EnsureTracking exists for. The usual names answer it instead.
    /// </summary>
    [Fact]
    public void WithoutThatRefTheUsualNamesAnswerIt()
    {
        using var repo = new TempRepository();

        repo.Write("file.txt", "one\n");
        repo.Commit("first");

        var trunk = repo.CurrentBranch();
        WorkOn(repo, "a-feature");

        // Only meaningful if git's own initial branch is one of the names we look for,
        // which it is - but the test says so rather than assuming it.
        Assert.Contains(trunk, new[] { "main", "master" });
        Assert.Equal(trunk, DefaultOf(repo));
    }

    [Fact]
    public void AProjectWithNoRecognisableTrunkFallsBackToWhereYouAre()
    {
        using var repo = new TempRepository();

        repo.Write("file.txt", "one\n");
        repo.Commit("first");

        using (var raw = new Repository(repo.Path))
        {
            var renamed = raw.Branches.Rename(raw.Head.FriendlyName, "release");
            Commands.Checkout(raw, renamed);
        }

        Assert.Equal("release", DefaultOf(repo));
    }

    /// <summary>The bug itself: on a feature branch, the default must not follow you.</summary>
    [Fact]
    public void TheBranchYouAreOnIsNotTheDefaultBranch()
    {
        using var repo = new TempRepository();

        repo.Write("file.txt", "one\n");
        repo.Commit("first");

        WorkOn(repo, "faster-diffs");

        Assert.Equal("faster-diffs", repo.CurrentBranch());
        Assert.NotEqual("faster-diffs", DefaultOf(repo));
    }

    [Fact]
    public void TheBranchListMarksTheSameBranchAsDefault()
    {
        using var repo = new TempRepository();

        repo.Write("file.txt", "one\n");
        repo.Commit("first");

        var trunk = repo.CurrentBranch();
        WorkOn(repo, "faster-diffs");

        var branches = Git.GetBranches(repo.Path);

        Assert.True(branches.Single(b => b.Name == trunk).IsDefault);
        Assert.False(branches.Single(b => b.Name == "faster-diffs").IsDefault);
    }
}
