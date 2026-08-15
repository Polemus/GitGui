using Omnigit.Models;
using Omnigit.Services;

namespace Omnigit.Tests;

/// <summary>
/// What the change list offers, and what committing those paths actually records.
/// The two have to agree: a change that never reaches the list can never be ticked,
/// so it survives every commit without anyone being told.
/// </summary>
public class StagingTests
{
    private static readonly IGitService Git = new GitService();

    [Fact]
    public void CommittingADeletedFileRecordsTheDeletion()
    {
        using var repo = new TempRepository();

        repo.Write("keep.txt", "keep\n");
        repo.Write("gone.txt", "gone\n");
        repo.Commit("first");

        File.Delete(Path.Combine(repo.Path, "gone.txt"));

        Git.Commit(repo.Path, ["gone.txt"], "remove gone", "");

        Assert.Empty(Git.GetWorkingChanges(repo.Path));
    }

    /// <summary>
    /// A moved file is a delete plus an add, and libgit2's rename detection would pair
    /// them into a single entry naming only the new path - which left the delete out of
    /// the list, out of the commit, and still sitting in the working tree afterwards.
    /// </summary>
    [Fact]
    public void MovingAFileListsBothHalvesAndCommitsBoth()
    {
        using var repo = new TempRepository();

        const string contents = "identical either side of the move\n";

        repo.Write("old/thing.txt", contents);
        repo.Commit("first");

        File.Delete(Path.Combine(repo.Path, "old/thing.txt"));
        repo.Write("new/thing.txt", contents);

        var changes = Git.GetWorkingChanges(repo.Path);

        Assert.Contains(changes, c => c.Path == "old/thing.txt" && c.Status == ChangeStatus.Deleted);
        Assert.Contains(changes, c => c.Path == "new/thing.txt" && c.Status == ChangeStatus.Added);

        Git.Commit(repo.Path, changes.Select(c => c.Path).ToList(), "moved thing", "");

        Assert.Empty(Git.GetWorkingChanges(repo.Path));
        Assert.False(repo.Exists("old/thing.txt"));
    }
}
