using LibGit2Sharp;
using Omnigit.Services;

namespace Omnigit.Tests;

/// <summary>
/// Fetching a pull request's head, against a real repository with a real remote.
/// A pull request's head is an ordinary ref on the remote - which is what makes a
/// contributor's fork checkable-out without adding it as a remote - so a bare
/// repository next door with a <c>refs/pull/…</c> ref in it is the whole of the setup.
/// </summary>
public class PullRequestCheckoutTests
{
    private static readonly IGitService Git = new GitService();

    /// <summary>A clone with an origin holding one pull request, numbered 1.</summary>
    private static TempRepository RepoWithPullRequest(out string headSha)
    {
        var repo = new TempRepository();

        repo.Write("file.txt", "one\n");
        repo.Commit("first");
        repo.AddOrigin();
        repo.PushWithoutUpstream();

        headSha = ProposeFrom(repo, "two\n", "the proposed change", 1);
        return repo;
    }

    /// <summary>
    /// Commits a change on a throwaway branch, pushes it to <c>refs/pull/n/head</c>, and
    /// puts the working copy back where it was - leaving the remote holding a pull
    /// request that no local branch knows about, exactly like one opened from a fork.
    /// </summary>
    private static string ProposeFrom(
        TempRepository repository, string contents, string message, int number, string? startPoint = null)
    {
        using var repo = new Repository(repository.Path);

        var trunk = repo.Head.FriendlyName;
        var signature = new Signature("Contributor", "them@example.com", DateTimeOffset.Now);

        // A second push to the same pull request has to build on the first, the way
        // addressing review comments does - the remote refuses to rewind the ref.
        var branch = startPoint is null
            ? repo.CreateBranch($"proposal-{number}")
            : repo.CreateBranch($"proposal-{number}", repo.Lookup<Commit>(startPoint));
        Commands.Checkout(repo, branch);

        File.WriteAllText(Path.Combine(repository.Path, "file.txt"), contents);
        Commands.Stage(repo, "*");
        var commit = repo.Commit(message, signature, signature);

        repo.Network.Push(repo.Network.Remotes["origin"],
            $"refs/heads/{branch.FriendlyName}:refs/pull/{number}/head");

        Commands.Checkout(repo, repo.Branches[trunk]);
        repo.Branches.Remove(branch);

        return commit.Sha;
    }

    private static string? RefTarget(TempRepository repository, string canonicalName)
    {
        using var repo = new Repository(repository.Path);
        return repo.Refs[canonicalName]?.ResolveToDirectReference()?.TargetIdentifier;
    }

    [Fact]
    public void FetchingBringsTheHeadDownWithoutTouchingAnyBranch()
    {
        using var repo = RepoWithPullRequest(out var proposed);

        var fetch = Git.FetchPullRequest(repo.Path, 1, null, credentials: null);

        Assert.True(fetch.Result.Succeeded, fetch.Result.Message);
        Assert.Equal("pr/1", fetch.BranchName);

        // Nothing local exists yet, so the caller has to create the branch on the way in.
        Assert.True(fetch.IsNew);
        Assert.False(fetch.IsStale);

        // Fetched into a remote-tracking ref, which is what a branch can then start at.
        Assert.Equal(proposed, RefTarget(repo, "refs/remotes/origin/pr/1"));
        Assert.Null(RefTarget(repo, "refs/heads/pr/1"));
    }

    /// <summary>
    /// The refspec names one ref. Pruning against it would take every other
    /// remote-tracking branch with it, since none of them are in that refspec.
    /// </summary>
    [Fact]
    public void FetchingOnePullRequestLeavesTheOtherRemoteBranchesAlone()
    {
        using var repo = RepoWithPullRequest(out _);

        var trunk = $"refs/remotes/origin/{repo.CurrentBranch()}";
        Assert.NotNull(RefTarget(repo, trunk));

        Git.FetchPullRequest(repo.Path, 1, null, credentials: null);

        Assert.NotNull(RefTarget(repo, trunk));
    }

    [Fact]
    public void ARefTemplateFromTheHostIsWhatGetsFetched()
    {
        using var repo = new TempRepository();

        repo.Write("file.txt", "one\n");
        repo.Commit("first");
        repo.AddOrigin();
        repo.PushWithoutUpstream();

        // GitLab keeps merge request heads somewhere else entirely.
        using (var raw = new Repository(repo.Path))
        {
            var signature = new Signature("Contributor", "them@example.com", DateTimeOffset.Now);
            var branch = raw.CreateBranch("mr");
            Commands.Checkout(raw, branch);
            File.WriteAllText(Path.Combine(repo.Path, "file.txt"), "merged?\n");
            Commands.Stage(raw, "*");
            raw.Commit("proposal", signature, signature);
            raw.Network.Push(raw.Network.Remotes["origin"], "refs/heads/mr:refs/merge-requests/4/head");
            Commands.Checkout(raw, raw.Branches[repo.CurrentBranch()]);
        }

        var fetch = Git.FetchPullRequest(
            repo.Path, 4, "refs/merge-requests/{number}/head", credentials: null);

        Assert.True(fetch.Result.Succeeded, fetch.Result.Message);
        Assert.Equal("pr/4", fetch.BranchName);
        Assert.NotNull(RefTarget(repo, "refs/remotes/origin/pr/4"));
    }

    [Fact]
    public void APullRequestTheRemoteHasNeverHeardOfIsReportedRatherThanThrown()
    {
        using var repo = RepoWithPullRequest(out _);

        var fetch = Git.FetchPullRequest(repo.Path, 99, null, credentials: null);

        Assert.False(fetch.Result.Succeeded);
        Assert.Contains("refs/pull/99/head", fetch.Result.Message);
    }

    /// <summary>
    /// Checking the same pull request out again after it has been added to. The branch
    /// is only behind, so moving it loses nothing - and leaving it where it was would
    /// mean quietly reviewing an old version.
    /// </summary>
    [Fact]
    public void CheckingOutAgainMovesTheBranchOnToWhatWasFetched()
    {
        using var repo = RepoWithPullRequest(out var first);

        var initial = Git.FetchPullRequest(repo.Path, 1, null, credentials: null);
        Assert.True(initial.IsNew);

        // What the caller does with IsNew: start the branch at the fetched ref.
        using (var raw = new Repository(repo.Path))
            raw.CreateBranch("pr/1", raw.Lookup<Commit>(first));

        var second = ProposeFrom(repo, "three\n", "addressed the review", 1, startPoint: first);

        var again = Git.FetchPullRequest(repo.Path, 1, null, credentials: null);

        Assert.True(again.Result.Succeeded, again.Result.Message);
        Assert.False(again.IsNew);
        Assert.False(again.IsStale);
        Assert.Equal(second, RefTarget(repo, "refs/heads/pr/1"));
    }

    /// <summary>
    /// A force-push, or local commits on top: the two have diverged, and moving the
    /// branch would throw one side away. It is left alone and the caller is told.
    /// </summary>
    [Fact]
    public void ABranchThatHasDivergedIsLeftAloneAndReportedStale()
    {
        using var repo = RepoWithPullRequest(out var proposed);

        Git.FetchPullRequest(repo.Path, 1, null, credentials: null);

        string local;
        using (var raw = new Repository(repo.Path))
        {
            var signature = new Signature("Me", "me@example.com", DateTimeOffset.Now);
            var branch = raw.CreateBranch("pr/1", raw.Lookup<Commit>(proposed));
            Commands.Checkout(raw, branch);

            File.WriteAllText(Path.Combine(repo.Path, "notes.txt"), "mine\n");
            Commands.Stage(raw, "*");
            local = raw.Commit("a commit of my own", signature, signature).Sha;

            Commands.Checkout(raw, raw.Branches[raw.Branches.First(b => b.IsRemote is false && b.FriendlyName != "pr/1").FriendlyName]);
        }

        var again = Git.FetchPullRequest(repo.Path, 1, null, credentials: null);

        Assert.True(again.Result.Succeeded, again.Result.Message);
        Assert.True(again.IsStale);
        Assert.Equal(local, RefTarget(repo, "refs/heads/pr/1"));
    }
}
