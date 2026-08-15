using LibGit2Sharp;
using Omnigit.Services;

namespace Omnigit.Tests;

/// <summary>
/// A branch is published when the remote has a branch of that name. Nothing else counts
/// - not an upstream pointing somewhere else, not a tracking config git carried across a
/// rename. If the name isn't there, it's a new branch.
/// </summary>
public class PublishBeforeProposingTests
{
    private static readonly IGitService Git = new GitService();

    /// <summary>
    /// Pushed under one name, then renamed locally with <c>git branch -m</c> - which
    /// leaves the upstream config pointing at the old name, in sync, on a branch whose
    /// new name is on no server anywhere.
    /// </summary>
    private static TempRepository RenamedAfterPushing(out string pushedAs, out string renamedTo)
    {
        var repo = new TempRepository();

        repo.Write("file.txt", "one\n");
        repo.Commit("first");
        repo.AddOrigin();

        var was = repo.CurrentBranch();
        pushedAs = was;
        repo.PushWithoutUpstream();

        var name = "renamed-after-pushing";
        renamedTo = name;

        using (var raw = new Repository(repo.Path))
        {
            var branch = raw.Branches.Rename(raw.Head.FriendlyName, name);

            // What git's own rename does, and the whole reason this test exists.
            raw.Branches.Update(branch,
                b => b.Remote = "origin",
                b => b.UpstreamBranch = $"refs/heads/{was}");

            Commands.Checkout(raw, raw.Branches[name]);
        }

        return repo;
    }

    [Fact]
    public void ARenamedBranchIsUnpublishedEvenThoughItStillTracksItsOldName()
    {
        using var repo = RenamedAfterPushing(out var pushedAs, out var renamedTo);

        using (var check = new Repository(repo.Path))
        {
            // The state being tested: git says tracking, in sync, upstream elsewhere.
            Assert.True(check.Head.IsTracking);
            Assert.Equal($"refs/heads/{pushedAs}", check.Head.UpstreamBranchCanonicalName);
        }

        Assert.False(Git.OpenRepository(repo.Path).IsPublished);

        using (var check = new Repository(repo.Path))
            Assert.Null(check.Branches[$"origin/{renamedTo}"]);
    }

    [Fact]
    public void PublishingSendsItToABranchOfTheSameName()
    {
        using var repo = RenamedAfterPushing(out var pushedAs, out var renamedTo);

        var push = Git.Push(repo.Path, credentials: null);

        Assert.True(push.Succeeded, push.Message);
        Assert.True(Git.OpenRepository(repo.Path).IsPublished);

        using var check = new Repository(repo.Path);

        // The new name, not the one it was renamed from.
        Assert.NotNull(check.Branches[$"origin/{renamedTo}"]);
        Assert.Equal($"refs/heads/{renamedTo}", check.Head.UpstreamBranchCanonicalName);

        // And the old branch is left alone rather than being written over.
        Assert.NotNull(check.Branches[$"origin/{pushedAs}"]);
    }

    [Fact]
    public void CommitsOnAnUnpublishedBranchAreNotCountedAgainstSomeOtherBranch()
    {
        using var repo = RenamedAfterPushing(out _, out _);

        repo.Write("file.txt", "two\n");
        repo.Commit("second");

        var info = Git.OpenRepository(repo.Path);

        // Ahead of nothing: there is no branch of this name to be ahead of. The toolbar
        // reads this as "Publish branch", which is the only honest offer here.
        Assert.False(info.IsPublished);
        Assert.True(info.HasRemote);
    }

    [Fact]
    public void AnOrdinaryPushedBranchStillReadsAsPublished()
    {
        using var repo = new TempRepository();

        repo.Write("file.txt", "one\n");
        repo.Commit("first");
        repo.AddOrigin();
        repo.PushWithoutUpstream();

        var info = Git.OpenRepository(repo.Path);

        Assert.True(info.IsPublished);
        Assert.Equal(0, info.Ahead);
    }

    [Fact]
    public void CommitsAfterPublishingCountAsAhead()
    {
        using var repo = new TempRepository();

        repo.Write("file.txt", "one\n");
        repo.Commit("first");
        repo.AddOrigin();
        repo.PushWithoutUpstream();

        repo.Write("file.txt", "two\n");
        repo.Commit("second");

        var info = Git.OpenRepository(repo.Path);

        Assert.True(info.IsPublished);
        Assert.Equal(1, info.Ahead);
    }
}
