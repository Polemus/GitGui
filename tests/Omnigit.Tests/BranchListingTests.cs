using Omnigit.Models;
using Omnigit.Services;

namespace Omnigit.Tests;

/// <summary>
/// What the branch picker is given. The list was local branches only, so a fresh clone
/// offered the one branch it had checked out and no way to reach any of the others -
/// checking out a colleague's branch meant leaving the app.
/// </summary>
public class BranchListingTests
{
    private static readonly IGitService Git = new GitService();

    private static TempRepository Published()
    {
        var repo = new TempRepository();
        repo.Write("a.txt", "one\n");
        repo.Commit("first");
        repo.AddOrigin();
        repo.PushWithoutUpstream();
        return repo;
    }

    private static BranchInfo? Find(IEnumerable<BranchInfo> branches, string name)
        => branches.FirstOrDefault(b => b.Name == name);

    [Fact]
    public void A_branch_only_on_the_remote_is_listed_under_its_short_name()
    {
        using var repo = Published();
        repo.AddRemoteOnlyBranch("feature", "b.txt", "two\n");

        var feature = Find(Git.GetBranches(repo.Path), "feature");

        Assert.NotNull(feature);
        Assert.True(feature.IsRemoteOnly);
        Assert.Equal("origin", feature.RemoteName);

        // The short name is what a checkout is asked for; the qualified one is for display.
        Assert.Equal("origin/feature", feature.QualifiedName);
    }

    [Fact]
    public void A_branch_on_both_sides_is_listed_once_and_is_not_remote_only()
    {
        using var repo = Published();

        var branches = Git.GetBranches(repo.Path);
        var current = repo.CurrentBranch();

        Assert.Single(branches, b => b.Name == current);
        Assert.False(Find(branches, current)!.IsRemoteOnly);
    }

    /// <summary>
    /// Neither is a branch anyone pushed. <c>origin/HEAD</c> names the default branch, and
    /// <c>origin/pr/1</c> is a mirror our own pull request fetch wrote - checking either
    /// out would make a local branch under a name the server has never heard of.
    /// </summary>
    [Fact]
    public void The_remote_HEAD_and_pull_request_mirrors_are_not_branches()
    {
        using var repo = Published();
        repo.AddRemoteRef("HEAD");
        repo.AddRemoteRef("pr/1");

        var names = Git.GetBranches(repo.Path).Select(b => b.Name).ToList();

        Assert.DoesNotContain("HEAD", names);
        Assert.DoesNotContain("pr/1", names);
    }

    [Fact]
    public void Checking_out_a_remote_only_branch_creates_it_here_tracking_the_remote()
    {
        using var repo = Published();
        repo.AddRemoteOnlyBranch("feature", "b.txt", "two\n");

        var result = Git.SwitchBranch(repo.Path, "feature", create: false, bringPaths: null);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("feature", repo.CurrentBranch());
        Assert.Equal("two\n", repo.Read("b.txt"));

        // Tracking is written now rather than left to the first push: the ref it was
        // created from is the one certain answer to what this branch follows.
        Assert.Equal("origin/feature", repo.UpstreamOf("feature"));
        Assert.Equal(repo.TipOf("origin/feature"), repo.TipOf("feature"));
    }

    [Fact]
    public void The_branch_it_created_is_then_an_ordinary_local_branch()
    {
        using var repo = Published();
        repo.AddRemoteOnlyBranch("feature", "b.txt", "two\n");

        Git.SwitchBranch(repo.Path, "feature", create: false, bringPaths: null);

        var feature = Find(Git.GetBranches(repo.Path), "feature");

        Assert.NotNull(feature);
        Assert.False(feature.IsRemoteOnly);
        Assert.True(feature.IsCurrent);
    }

    /// <summary>
    /// The refusal is worked out by comparing what is committed on either side, so the
    /// side that only exists on the remote has to be resolved the same way the checkout
    /// resolves it - otherwise the check silently passes and the file is overwritten.
    /// </summary>
    [Fact]
    public void Carrying_a_file_that_differs_on_a_remote_only_branch_is_refused()
    {
        using var repo = Published();
        repo.AddRemoteOnlyBranch("feature", "a.txt", "changed on the branch\n");

        repo.Write("a.txt", "changed here\n");

        var result = Git.SwitchBranch(repo.Path, "feature", create: false, bringPaths: ["a.txt"]);

        Assert.False(result.Succeeded);
        Assert.Contains("a.txt", result.Message);

        // Refused rather than half-done: nothing moved.
        Assert.NotEqual("feature", repo.CurrentBranch());
        Assert.Equal("changed here\n", repo.Read("a.txt"));
    }
}
