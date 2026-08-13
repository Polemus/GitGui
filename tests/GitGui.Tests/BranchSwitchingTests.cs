using GitGui.Services;

namespace GitGui.Tests;

/// <summary>
/// Switching branches with uncommitted work. Bringing only some files across needs two
/// stashes and several steps, and getting it wrong loses work that was never committed,
/// so every path is exercised against a real repository.
/// </summary>
public class BranchSwitchingTests
{
    private static readonly IGitService Git = new GitService();

    private static TempRepository RepoWithCommit()
    {
        var repo = new TempRepository();
        repo.Write("kept.txt", "original\n");
        repo.Write("other.txt", "original\n");
        repo.Commit("first");
        return repo;
    }

    [Fact]
    public void BringingEverythingCarriesTheChangesAndStashesNothing()
    {
        using var repo = RepoWithCommit();
        repo.Write("kept.txt", "modified\n");
        repo.Write("new.txt", "brand new\n");

        Git.SwitchBranch(repo.Path, "feature", create: true, bringPaths: null);

        Assert.Equal("feature", repo.CurrentBranch());
        Assert.Equal("modified\n", repo.Read("kept.txt"));
        Assert.Equal("brand new\n", repo.Read("new.txt"));
        Assert.Equal(0, repo.StashCount());
    }

    [Fact]
    public void LeavingEverythingStashesItAndCleansTheTree()
    {
        using var repo = RepoWithCommit();
        repo.Write("kept.txt", "modified\n");
        repo.Write("new.txt", "brand new\n");

        Git.SwitchBranch(repo.Path, "feature", create: true, bringPaths: []);

        Assert.Equal("feature", repo.CurrentBranch());
        Assert.Equal("original\n", repo.Read("kept.txt"));
        Assert.False(repo.Exists("new.txt"));
        Assert.Equal(1, repo.StashCount());
    }

    [Fact]
    public void BringingSomeFilesCarriesThoseAndStashesTheRest()
    {
        using var repo = RepoWithCommit();
        repo.Write("kept.txt", "carried\n");
        repo.Write("other.txt", "left behind\n");

        Git.SwitchBranch(repo.Path, "feature", create: true, bringPaths: ["kept.txt"]);

        Assert.Equal("feature", repo.CurrentBranch());

        // The carried file arrives modified; the other is back to its committed state.
        Assert.Equal("carried\n", repo.Read("kept.txt"));
        Assert.Equal("original\n", repo.Read("other.txt"));

        // Exactly one stash - the intermediate full one must have been dropped.
        Assert.Equal(1, repo.StashCount());
    }

    [Fact]
    public void TheStashLeftBehindContainsOnlyWhatWasLeft()
    {
        using var repo = RepoWithCommit();
        var original = repo.CurrentBranch();

        repo.Write("kept.txt", "carried\n");
        repo.Write("other.txt", "left behind\n");

        Git.SwitchBranch(repo.Path, "feature", create: true, bringPaths: ["kept.txt"]);

        // Put the carried change away so popping cannot conflict with it.
        Git.Commit(repo.Path, ["kept.txt"], "carry", string.Empty);
        Git.SwitchBranch(repo.Path, original, create: false, bringPaths: null);

        Git.PopStash(repo.Path, 0);

        // Only the left-behind change comes back. If the full stash had been kept,
        // kept.txt would have been dragged back to "carried" as well.
        Assert.Equal("left behind\n", repo.Read("other.txt"));
        Assert.Equal("original\n", repo.Read("kept.txt"));
        Assert.Equal(0, repo.StashCount());
    }

    [Fact]
    public void UntrackedFilesCanBeCarriedSelectively()
    {
        using var repo = RepoWithCommit();
        repo.Write("carried-new.txt", "one\n");
        repo.Write("left-new.txt", "two\n");

        Git.SwitchBranch(repo.Path, "feature", create: true, bringPaths: ["carried-new.txt"]);

        Assert.True(repo.Exists("carried-new.txt"));
        Assert.Equal("one\n", repo.Read("carried-new.txt"));

        // An untracked file has no committed version, so leaving it behind means
        // removing it from the tree entirely.
        Assert.False(repo.Exists("left-new.txt"));
        Assert.Equal(1, repo.StashCount());
    }

    [Fact]
    public void SwitchingWithNoChangesAtAllTouchesNothing()
    {
        using var repo = RepoWithCommit();

        Git.SwitchBranch(repo.Path, "feature", create: true, bringPaths: []);

        Assert.Equal("feature", repo.CurrentBranch());
        Assert.Equal(0, repo.StashCount());
    }

    [Fact]
    public void StashesRecordTheBranchTheyCameFrom()
    {
        using var repo = RepoWithCommit();
        var original = repo.CurrentBranch();

        repo.Write("kept.txt", "modified\n");
        Git.SwitchBranch(repo.Path, "feature", create: true, bringPaths: []);

        var stash = Assert.Single(Git.GetStashes(repo.Path));

        Assert.Equal(original, stash.BranchName);
        Assert.Equal(0, stash.Index);
    }

    [Fact]
    public void DroppingAStashRemovesItWithoutRestoringAnything()
    {
        using var repo = RepoWithCommit();
        repo.Write("kept.txt", "modified\n");

        Git.SwitchBranch(repo.Path, "feature", create: true, bringPaths: []);
        Git.DropStash(repo.Path, 0);

        Assert.Empty(Git.GetStashes(repo.Path));
        Assert.Equal("original\n", repo.Read("kept.txt"));
    }

    [Fact]
    public void CreatingABranchThatExistsIsRefused()
    {
        using var repo = RepoWithCommit();
        var original = repo.CurrentBranch();

        Git.CreateBranch(repo.Path, "feature");
        Git.SwitchBranch(repo.Path, original, create: false, bringPaths: null);

        Assert.Throws<InvalidOperationException>(
            () => Git.SwitchBranch(repo.Path, "feature", create: true, bringPaths: null));
    }

    [Fact]
    public void SwitchingToAMissingBranchIsRefused()
    {
        using var repo = RepoWithCommit();

        Assert.Throws<InvalidOperationException>(
            () => Git.SwitchBranch(repo.Path, "nope", create: false, bringPaths: null));
    }

    // ---- Files that differ on both branches --------------------------------
    // git only carries uncommitted work across when the file is identical on the target
    // branch. When it isn't, libgit2 refuses with a CheckoutConflictException - which is
    // a question for the user, not a fault, so it comes back as a result.

    /// <summary>Two branches whose copies of <c>kept.txt</c> have diverged.</summary>
    private static TempRepository RepoWithDivergedFile(out string original, out string other)
    {
        var repo = RepoWithCommit();
        original = repo.CurrentBranch();

        Git.SwitchBranch(repo.Path, "feature", create: true, bringPaths: null);
        repo.Write("kept.txt", "feature version\n");
        repo.Commit("feature edit");

        Git.SwitchBranch(repo.Path, original, create: false, bringPaths: null);
        other = "feature";
        return repo;
    }

    [Fact]
    public void CarryingAFileThatDiffersOnTheTargetIsRefused()
    {
        using var repo = RepoWithDivergedFile(out _, out var feature);
        repo.Write("kept.txt", "local edit\n");

        var result = Git.SwitchBranch(repo.Path, feature, create: false, bringPaths: null);

        Assert.False(result.Succeeded);
        Assert.Equal(SwitchOutcome.Conflicts, result.Outcome);
        Assert.Equal(["kept.txt"], result.ConflictingPaths);
    }

    [Fact]
    public void ARefusedSwitchLeavesTheWorkingTreeAlone()
    {
        using var repo = RepoWithDivergedFile(out var original, out var feature);
        repo.Write("kept.txt", "local edit\n");
        repo.Write("other.txt", "also edited\n");

        Git.SwitchBranch(repo.Path, feature, create: false, bringPaths: null);

        Assert.Equal(original, repo.CurrentBranch());
        Assert.Equal("local edit\n", repo.Read("kept.txt"));
        Assert.Equal("also edited\n", repo.Read("other.txt"));
        Assert.Equal(0, repo.StashCount());
    }

    [Fact]
    public void LeavingTheConflictingFileBehindStashesItAndSwitches()
    {
        using var repo = RepoWithDivergedFile(out _, out var feature);
        repo.Write("kept.txt", "local edit\n");

        var result = Git.SwitchBranch(repo.Path, feature, create: false, bringPaths: []);

        Assert.True(result.Succeeded);
        Assert.Equal(feature, repo.CurrentBranch());
        Assert.Equal("feature version\n", repo.Read("kept.txt"));
        Assert.Equal(1, repo.StashCount());
    }

    [Fact]
    public void CarryingAnUntrackedFileTheTargetBranchAlreadyHasIsRefused()
    {
        using var repo = RepoWithCommit();
        var original = repo.CurrentBranch();

        Git.SwitchBranch(repo.Path, "feature", create: true, bringPaths: null);
        repo.Write("added.txt", "committed on feature\n");
        repo.Commit("adds a file");
        Git.SwitchBranch(repo.Path, original, create: false, bringPaths: null);

        // Untracked here, but committed over there - checkout would overwrite it.
        repo.Write("added.txt", "untracked locally\n");

        var result = Git.SwitchBranch(repo.Path, "feature", create: false, bringPaths: null);

        Assert.False(result.Succeeded);
        Assert.Equal(["added.txt"], result.ConflictingPaths);
        Assert.Equal("untracked locally\n", repo.Read("added.txt"));
    }

    [Fact]
    public void CarryingAFileThatIsTheSameOnBothBranchesStillWorks()
    {
        using var repo = RepoWithDivergedFile(out _, out var feature);

        // other.txt never diverged, so carrying it across is fine even though
        // kept.txt differs - kept.txt just isn't dirty here.
        repo.Write("other.txt", "local edit\n");

        var result = Git.SwitchBranch(repo.Path, feature, create: false, bringPaths: null);

        Assert.True(result.Succeeded);
        Assert.Equal(feature, repo.CurrentBranch());
        Assert.Equal("local edit\n", repo.Read("other.txt"));
    }
}
