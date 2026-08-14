using Omnigit.Models;
using Omnigit.Services;

namespace Omnigit.Tests;

/// <summary>
/// What the sync button is told about a branch. The case that matters is a branch pushed
/// without <c>-u</c>: git leaves no upstream behind, LibGit2Sharp's TrackingDetails then
/// answers null to everything, and reading that as zero made Omnigit offer a fetch over
/// commits that were sitting there waiting to be pushed.
/// </summary>
public class SyncStateTests
{
    private static RepositoryInfo Open(TempRepository repo)
        => new GitService().OpenRepository(repo.Path);

    private static TempRepository Published()
    {
        var repo = new TempRepository();
        repo.Write("a.txt", "one");
        repo.Commit("first");
        repo.AddOrigin();
        repo.PushWithoutUpstream();
        return repo;
    }

    [Fact]
    public void A_branch_pushed_without_upstream_still_counts_its_unpushed_commits()
    {
        using var repo = Published();
        repo.Write("b.txt", "two");
        repo.Commit("second");

        var info = Open(repo);

        Assert.False(repo.IsTracking(), "the test is pointless if something set an upstream");
        Assert.Equal(1, info.Ahead);
        Assert.Equal(0, info.Behind);
        Assert.True(info.IsPublished);
    }

    [Fact]
    public void A_branch_pushed_without_upstream_still_counts_what_it_is_missing()
    {
        using var repo = Published();
        var first = repo.HeadSha();

        repo.Write("b.txt", "two");
        repo.Commit("second");
        repo.PushWithoutUpstream();

        // The remote-tracking ref stays on "second" while the branch goes back a commit,
        // which is the shape a fetch leaves behind.
        repo.ResetHardTo(first);

        var info = Open(repo);

        Assert.Equal(0, info.Ahead);
        Assert.Equal(1, info.Behind);
    }

    [Fact]
    public void A_branch_the_remote_has_never_seen_is_unpublished()
    {
        using var repo = new TempRepository();
        repo.Write("a.txt", "one");
        repo.Commit("first");
        repo.AddOrigin();

        var info = Open(repo);

        Assert.True(info.HasRemote);
        Assert.False(info.IsPublished);
        Assert.Equal(0, info.Ahead);
    }

    [Fact]
    public void A_repository_with_no_remote_has_nowhere_to_publish_to()
    {
        using var repo = new TempRepository();
        repo.Write("a.txt", "one");
        repo.Commit("first");

        var info = Open(repo);

        Assert.False(info.HasRemote);
        Assert.False(info.IsPublished);
    }

    [Fact]
    public void A_properly_tracking_branch_reports_the_same_counts_as_before()
    {
        using var repo = Published();
        repo.SetUpstream();

        repo.Write("b.txt", "two");
        repo.Commit("second");

        var info = Open(repo);

        Assert.True(repo.IsTracking());
        Assert.Equal(1, info.Ahead);
        Assert.True(info.IsPublished);
    }

    [Fact]
    public void A_branch_that_is_level_with_the_remote_is_neither_ahead_nor_behind()
    {
        using var repo = Published();

        var info = Open(repo);

        Assert.Equal(0, info.Ahead);
        Assert.Equal(0, info.Behind);
        Assert.True(info.IsPublished);
    }

    [Fact]
    public void Pushing_records_the_tracking_config_git_never_wrote()
    {
        using var repo = Published();
        repo.Write("b.txt", "two");
        repo.Commit("second");

        var result = new GitService().Push(repo.Path, credentials: null);

        Assert.Equal(SyncOutcome.Succeeded, result.Outcome);
        Assert.True(repo.IsTracking(), "the push should have left an upstream behind");
    }

    [Fact]
    public void Publishing_a_branch_the_remote_has_never_seen_is_a_push()
    {
        using var repo = new TempRepository();
        repo.Write("a.txt", "one");
        repo.Commit("first");
        repo.AddOrigin();

        var result = new GitService().Push(repo.Path, credentials: null);

        Assert.Equal(SyncOutcome.Succeeded, result.Outcome);
        Assert.True(repo.IsTracking());
        Assert.True(Open(repo).IsPublished);
    }
}
