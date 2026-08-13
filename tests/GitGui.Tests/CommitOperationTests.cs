using GitGui.Models;
using GitGui.Services;
using LibGit2Sharp;

namespace GitGui.Tests;

/// <summary>
/// Tagging, opening, undoing and copying commits, against real repositories.
/// </summary>
/// <remarks>
/// These go through libgit2 rather than a stub because the interesting part of every
/// one of them is what libgit2 leaves behind - a detached HEAD, a half-applied revert,
/// an index with three entries for one path - and a stub could only assert what we
/// already believe.
/// </remarks>
public class CommitOperationTests
{
    private static readonly IGitService Git = new GitService();

    /// <summary>Three commits, each changing <c>file.txt</c>, so any two of them clash.</summary>
    private static TempRepository RepoWithThreeVersions()
    {
        var repo = new TempRepository();

        repo.Write("file.txt", "one\n");
        repo.Commit("first");

        repo.Write("file.txt", "two\n");
        repo.Commit("second");

        repo.Write("file.txt", "three\n");
        repo.Commit("third");

        return repo;
    }

    // ------------------------------------------------------------------ tags

    [Fact]
    public void TaggingACommitShowsTheTagInTheHistory()
    {
        using var repo = RepoWithThreeVersions();
        var older = repo.Shas()[1];

        Git.CreateTag(repo.Path, "v1.0.0", older, message: null);

        var tagged = Git.GetHistory(repo.Path, 10).Single(c => c.Sha == older);

        Assert.Equal(["v1.0.0"], tagged.Tags);
        Assert.Null(repo.TagMessage("v1.0.0"));
    }

    [Fact]
    public void AMessageMakesAnAnnotatedTag()
    {
        using var repo = RepoWithThreeVersions();

        Git.CreateTag(repo.Path, "v2.0.0", repo.HeadSha(), "ready to ship");

        Assert.Equal("ready to ship", repo.TagMessage("v2.0.0")?.Trim());
    }

    [Fact]
    public void TaggingTwiceWithTheSameNameIsRefused()
    {
        using var repo = RepoWithThreeVersions();
        var shas = repo.Shas();

        Git.CreateTag(repo.Path, "v1.0.0", shas[0], message: null);

        Assert.Throws<InvalidOperationException>(
            () => Git.CreateTag(repo.Path, "v1.0.0", shas[1], message: null));
    }

    [Fact]
    public void ANameGitWillNotAcceptIsRefusedBeforeAnythingIsWritten()
    {
        using var repo = RepoWithThreeVersions();

        // Spaces are not allowed in a ref name; libgit2's own complaint names the
        // internal ref path rather than the tag, so it is translated.
        Assert.Throws<InvalidOperationException>(
            () => Git.CreateTag(repo.Path, "not a tag", repo.HeadSha(), message: null));

        Assert.Empty(Git.GetHistory(repo.Path, 10).SelectMany(c => c.Tags));
    }

    [Fact]
    public void TaggingACommitThatIsNotHereIsRefused()
    {
        using var repo = RepoWithThreeVersions();

        Assert.Throws<InvalidOperationException>(
            () => Git.CreateTag(repo.Path, "v1.0.0", new string('a', 40), message: null));
    }

    // -------------------------------------------------------- opening commits

    [Fact]
    public void OpeningACommitDetachesHeadOntoIt()
    {
        using var repo = RepoWithThreeVersions();
        var older = repo.Shas()[1];

        var result = Git.CheckoutCommit(repo.Path, older);

        Assert.True(result.Succeeded);
        Assert.Equal("two\n", repo.Read("file.txt"));

        var info = Git.OpenRepository(repo.Path);

        Assert.True(info.IsDetached);
        Assert.Equal(older, info.HeadSha);
    }

    [Fact]
    public void OpeningACommitWithUncommittedWorkIsRefused()
    {
        using var repo = RepoWithThreeVersions();
        repo.Write("file.txt", "uncommitted\n");

        var result = Git.CheckoutCommit(repo.Path, repo.Shas()[1]);

        Assert.False(result.Succeeded);

        // Nothing moved and nothing was lost.
        Assert.Equal("uncommitted\n", repo.Read("file.txt"));
        Assert.False(Git.OpenRepository(repo.Path).IsDetached);
    }

    [Fact]
    public void ABranchMadeWhileDetachedIsBackOnABranchAgain()
    {
        using var repo = RepoWithThreeVersions();
        Git.CheckoutCommit(repo.Path, repo.Shas()[1]);

        Git.SwitchBranch(repo.Path, "from-here", create: true, bringPaths: null);

        var info = Git.OpenRepository(repo.Path);

        Assert.False(info.IsDetached);
        Assert.Equal("from-here", repo.CurrentBranch());
        Assert.Equal("two\n", repo.Read("file.txt"));
    }

    // --------------------------------------------------------------- reverting

    [Fact]
    public void RevertingUndoesTheCommitAndRecordsItAsANewOne()
    {
        using var repo = new TempRepository();
        repo.Write("kept.txt", "kept\n");
        repo.Commit("first");

        repo.Write("added.txt", "added\n");
        repo.Commit("adds a file");
        var adding = repo.HeadSha();

        repo.Write("later.txt", "later\n");
        repo.Commit("unrelated");

        var result = Git.RevertCommit(repo.Path, adding);

        Assert.True(result.Succeeded);
        Assert.False(repo.Exists("added.txt"));

        // Undone by adding to history, not by rewriting it: the original is still there.
        Assert.Contains(adding, repo.Shas());
        Assert.Equal(4, repo.Shas().Count);
    }

    [Fact]
    public void RevertingSomethingAlreadyUndoneChangesNothing()
    {
        using var repo = new TempRepository();
        repo.Write("kept.txt", "kept\n");
        repo.Commit("first");

        repo.Write("added.txt", "added\n");
        repo.Commit("adds a file");
        var adding = repo.HeadSha();

        Git.RevertCommit(repo.Path, adding);
        var before = repo.Shas().Count;

        var result = Git.RevertCommit(repo.Path, adding);

        Assert.Equal(CommitOperationOutcome.NothingToDo, result.Outcome);
        Assert.Equal(before, repo.Shas().Count);
    }

    [Fact]
    public void RevertingIntoAConflictStopsAndNamesTheFiles()
    {
        using var repo = RepoWithThreeVersions();
        var middle = repo.Shas()[1];

        var result = Git.RevertCommit(repo.Path, middle);

        Assert.Equal(CommitOperationOutcome.Conflicts, result.Outcome);
        Assert.Equal(["file.txt"], result.ConflictingPaths);
        Assert.Equal(["file.txt"], Git.GetConflictedPaths(repo.Path));

        var info = Git.OpenRepository(repo.Path);

        Assert.Equal(RepositoryOperation.Revert, info.Operation);
        Assert.Equal(1, info.ConflictCount);
    }

    [Fact]
    public void RevertingWithUncommittedWorkIsRefusedBeforeAnythingHappens()
    {
        using var repo = RepoWithThreeVersions();
        repo.Write("scratch.txt", "in progress\n");

        var result = Git.RevertCommit(repo.Path, repo.Shas()[1]);

        Assert.Equal(CommitOperationOutcome.Refused, result.Outcome);
        Assert.Equal(CurrentOperation.None, repo.Operation());
        Assert.Equal("in progress\n", repo.Read("scratch.txt"));
    }

    [Fact]
    public void RevertingWhileDetachedIsRefused()
    {
        using var repo = RepoWithThreeVersions();
        var shas = repo.Shas();

        Git.CheckoutCommit(repo.Path, shas[1]);

        var result = Git.RevertCommit(repo.Path, shas[1]);

        Assert.Equal(CommitOperationOutcome.Refused, result.Outcome);
    }

    // ------------------------------------------------------------ cherry-pick

    /// <summary>A <c>feature</c> branch made before the current branch moved on.</summary>
    private static TempRepository RepoWithABranchBehind(out string main)
    {
        var repo = new TempRepository();
        repo.Write("file.txt", "base\n");
        repo.Commit("first");

        main = repo.CurrentBranch();

        Git.SwitchBranch(repo.Path, "feature", create: true, bringPaths: null);
        Git.SwitchBranch(repo.Path, main, create: false, bringPaths: null);

        return repo;
    }

    [Fact]
    public void CherryPickingCopiesTheCommitOntoTheOtherBranch()
    {
        using var repo = RepoWithABranchBehind(out _);

        repo.Write("picked.txt", "carried across\n");
        repo.Commit("adds picked.txt");

        var result = Git.CherryPickCommit(repo.Path, repo.HeadSha(), "feature");

        Assert.True(result.Succeeded);

        // It lands on the branch it was asked for, and we are left standing there.
        Assert.Equal("feature", repo.CurrentBranch());
        Assert.Equal("carried across\n", repo.Read("picked.txt"));
    }

    [Fact]
    public void CherryPickingWithUncommittedWorkIsRefused()
    {
        using var repo = RepoWithABranchBehind(out var main);

        repo.Write("picked.txt", "carried across\n");
        repo.Commit("adds picked.txt");
        repo.Write("scratch.txt", "in progress\n");

        var result = Git.CherryPickCommit(repo.Path, repo.HeadSha(), "feature");

        Assert.Equal(CommitOperationOutcome.Refused, result.Outcome);
        Assert.Equal(main, repo.CurrentBranch());
    }

    [Fact]
    public void CherryPickingIntoAConflictStopsOnTheBranchItWasApplyingTo()
    {
        using var repo = new TempRepository();
        repo.Write("file.txt", "base\n");
        repo.Commit("first");

        var main = repo.CurrentBranch();

        Git.SwitchBranch(repo.Path, "feature", create: true, bringPaths: null);
        repo.Write("file.txt", "feature version\n");
        repo.Commit("feature edit");

        Git.SwitchBranch(repo.Path, main, create: false, bringPaths: null);
        repo.Write("file.txt", "main version\n");
        repo.Commit("main edit");

        var result = Git.CherryPickCommit(repo.Path, repo.HeadSha(), "feature");

        Assert.Equal(CommitOperationOutcome.Conflicts, result.Outcome);
        Assert.Equal(["file.txt"], result.ConflictingPaths);

        // Left mid-operation on the target branch, which is where the mess is.
        Assert.Equal("feature", repo.CurrentBranch());
        Assert.Equal(RepositoryOperation.CherryPick, Git.OpenRepository(repo.Path).Operation);
    }

    [Fact]
    public void CherryPickingOntoAMissingBranchIsRefused()
    {
        using var repo = RepoWithABranchBehind(out _);

        Assert.Throws<InvalidOperationException>(
            () => Git.CherryPickCommit(repo.Path, repo.HeadSha(), "nope"));
    }

    // --------------------------------------------------------------- resetting

    [Fact]
    public void ASoftResetMovesTheBranchAndLeavesEverythingStaged()
    {
        using var repo = RepoWithThreeVersions();
        var older = repo.Shas()[1];

        Git.ResetToCommit(repo.Path, older, ResetKind.Soft);

        Assert.Equal(older, repo.HeadSha());
        Assert.Equal("three\n", repo.Read("file.txt"));
        Assert.Equal(FileStatus.ModifiedInIndex, repo.StatusOf("file.txt"));
    }

    [Fact]
    public void AMixedResetLeavesTheChangesUnstaged()
    {
        using var repo = RepoWithThreeVersions();
        var older = repo.Shas()[1];

        Git.ResetToCommit(repo.Path, older, ResetKind.Mixed);

        Assert.Equal(older, repo.HeadSha());
        Assert.Equal("three\n", repo.Read("file.txt"));
        Assert.Equal(FileStatus.ModifiedInWorkdir, repo.StatusOf("file.txt"));
    }

    [Fact]
    public void AHardResetThrowsTheChangesAway()
    {
        using var repo = RepoWithThreeVersions();
        var older = repo.Shas()[1];

        Git.ResetToCommit(repo.Path, older, ResetKind.Hard);

        Assert.Equal(older, repo.HeadSha());
        Assert.Equal("two\n", repo.Read("file.txt"));
        Assert.Equal(FileStatus.Unaltered, repo.StatusOf("file.txt"));
    }

    // --------------------------------------------------------------- conflicts

    /// <summary>A repository stopped part-way through reverting, with one conflict.</summary>
    private static TempRepository RepoMidRevert(out string conflicted)
    {
        var repo = RepoWithThreeVersions();
        Git.RevertCommit(repo.Path, repo.Shas()[1]);
        conflicted = "file.txt";
        return repo;
    }

    [Fact]
    public void KeepingMyVersionLeavesTheFileAsItWas()
    {
        using var repo = RepoMidRevert(out var file);

        Git.ResolveConflict(repo.Path, file, ConflictSide.Mine);

        Assert.Equal("three\n", repo.Read(file));
        Assert.Empty(Git.GetConflictedPaths(repo.Path));
    }

    [Fact]
    public void KeepingTheirVersionTakesWhatTheRevertWanted()
    {
        using var repo = RepoMidRevert(out var file);

        Git.ResolveConflict(repo.Path, file, ConflictSide.Theirs);

        // Undoing "one -> two" means the file should read "one" again.
        Assert.Equal("one\n", repo.Read(file));
        Assert.Empty(Git.GetConflictedPaths(repo.Path));
    }

    [Fact]
    public void KeepingASideThatDeletedTheFileRemovesIt()
    {
        using var repo = new TempRepository();
        repo.Write("file.txt", "one\n");
        repo.Commit("first");

        repo.Write("file.txt", "two\n");
        repo.Commit("second");
        var second = repo.HeadSha();

        // Deleting the file here means reverting "second" has nothing to put back into.
        File.Delete(Path.Combine(repo.Path, "file.txt"));
        repo.Commit("removes the file");

        var result = Git.RevertCommit(repo.Path, second);
        Assert.Equal(CommitOperationOutcome.Conflicts, result.Outcome);

        Git.ResolveConflict(repo.Path, "file.txt", ConflictSide.Mine);

        Assert.False(repo.Exists("file.txt"));
        Assert.Empty(Git.GetConflictedPaths(repo.Path));
    }

    [Fact]
    public void MarkingAFileResolvedAcceptsWhateverIsInTheTree()
    {
        using var repo = RepoMidRevert(out var file);
        repo.Write(file, "hand merged\n");

        Git.MarkConflictResolved(repo.Path, [file]);

        Assert.Empty(Git.GetConflictedPaths(repo.Path));
        Assert.Equal("hand merged\n", repo.Read(file));
    }

    [Fact]
    public void ResolvingAFileThatIsNotConflictedIsRefused()
    {
        using var repo = RepoWithThreeVersions();

        Assert.Throws<InvalidOperationException>(
            () => Git.ResolveConflict(repo.Path, "file.txt", ConflictSide.Mine));
    }

    [Fact]
    public void AConflictedFileIsListedAsConflictedInTheChangeList()
    {
        using var repo = RepoMidRevert(out var file);

        // The panel that resolves conflicts is driven from the index, but the file also
        // has to read correctly in the ordinary change list beside it - it must not turn
        // up looking like a new file, which is what an unhandled status would do.
        var change = Assert.Single(Git.GetWorkingChanges(repo.Path));

        Assert.Equal(file, change.Path);
        Assert.True(change.IsConflicted);
        Assert.Equal("!", change.StatusGlyph);
    }

    [Fact]
    public void HistoryStillLoadsWhileDetached()
    {
        using var repo = RepoWithThreeVersions();
        var older = repo.Shas()[1];

        Git.CheckoutCommit(repo.Path, older);

        // The sidebar reads history from HEAD, so a detached HEAD has to walk from the
        // commit itself - and its first entry is what "make a branch here" acts on.
        var history = Git.GetHistory(repo.Path, 10);

        Assert.Equal(2, history.Count);
        Assert.Equal(older, history[0].Sha);
    }

    [Fact]
    public void GitOffersTheMessageItPreparedForTheFinalCommit()
    {
        using var repo = RepoMidRevert(out _);

        var message = Git.GetPendingMessage(repo.Path);

        Assert.NotNull(message);
        Assert.StartsWith("Revert", message.Value.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CommittingTheResolvedFilesEndsTheOperation()
    {
        using var repo = RepoMidRevert(out var file);
        Git.ResolveConflict(repo.Path, file, ConflictSide.Theirs);

        Git.Commit(repo.Path, [file], "Revert \"second\"", string.Empty);

        // Committing has to clear git's record that a revert was under way, or the app
        // would go on showing the conflict banner over a clean tree.
        Assert.Equal(CurrentOperation.None, repo.Operation());
        Assert.Equal(RepositoryOperation.None, Git.OpenRepository(repo.Path).Operation);
        Assert.Empty(Git.GetWorkingChanges(repo.Path));
    }

    [Fact]
    public void AbandoningPutsTheTreeBackAndForgetsTheOperation()
    {
        using var repo = RepoMidRevert(out var file);
        var head = repo.HeadSha();

        Git.AbortOperation(repo.Path);

        Assert.Equal(head, repo.HeadSha());
        Assert.Equal("three\n", repo.Read(file));
        Assert.Equal(CurrentOperation.None, repo.Operation());
        Assert.Empty(Git.GetConflictedPaths(repo.Path));
        Assert.Empty(Git.GetWorkingChanges(repo.Path));
    }

    [Fact]
    public void AnotherOperationIsRefusedWhileOneIsStillOpen()
    {
        using var repo = RepoMidRevert(out _);

        var result = Git.RevertCommit(repo.Path, repo.Shas()[0]);

        Assert.Equal(CommitOperationOutcome.Refused, result.Outcome);
        Assert.Contains("revert", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
