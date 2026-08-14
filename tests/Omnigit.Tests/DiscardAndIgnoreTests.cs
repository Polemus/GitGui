using Omnigit.Services;

namespace Omnigit.Tests;

/// <summary>
/// Discarding is the other operation here that destroys work with nothing to recover it
/// from: the change was never committed, so a bug means it is simply gone. The untracked
/// case is the one worth the care - those files have nothing in HEAD to be checked out
/// over, so a plain checkout would leave them sitting there looking discarded-but-not.
/// </summary>
public class DiscardAndIgnoreTests
{
    private static readonly IGitService Git = new GitService();

    private static TempRepository RepoWithCommit()
    {
        var repo = new TempRepository();
        repo.Write("kept.txt", "original\n");
        repo.Write("src/nested.txt", "original\n");
        repo.Commit("first");
        return repo;
    }

    [Fact]
    public void DiscardingATrackedFileRestoresItsCommittedContents()
    {
        using var repo = RepoWithCommit();
        repo.Write("kept.txt", "modified\n");

        Git.DiscardChanges(repo.Path, ["kept.txt"]);

        Assert.Equal("original\n", repo.Read("kept.txt"));
    }

    [Fact]
    public void DiscardingAnUntrackedFileDeletesIt()
    {
        using var repo = RepoWithCommit();
        repo.Write("scratch.txt", "never committed\n");

        Git.DiscardChanges(repo.Path, ["scratch.txt"]);

        Assert.False(repo.Exists("scratch.txt"));
    }

    [Fact]
    public void DiscardingADeletedFileBringsItBack()
    {
        using var repo = RepoWithCommit();
        File.Delete(Path.Combine(repo.Path, "kept.txt"));

        Git.DiscardChanges(repo.Path, ["kept.txt"]);

        Assert.True(repo.Exists("kept.txt"));
        Assert.Equal("original\n", repo.Read("kept.txt"));
    }

    [Fact]
    public void DiscardingOneFileLeavesTheOthersAlone()
    {
        using var repo = RepoWithCommit();
        repo.Write("kept.txt", "modified\n");
        repo.Write("src/nested.txt", "also modified\n");
        repo.Write("scratch.txt", "untracked\n");

        Git.DiscardChanges(repo.Path, ["kept.txt"]);

        Assert.Equal("original\n", repo.Read("kept.txt"));
        Assert.Equal("also modified\n", repo.Read("src/nested.txt"));
        Assert.True(repo.Exists("scratch.txt"));
    }

    [Fact]
    public void DiscardingHandlesTrackedAndUntrackedTogether()
    {
        using var repo = RepoWithCommit();
        repo.Write("kept.txt", "modified\n");
        repo.Write("scratch.txt", "untracked\n");

        Git.DiscardChanges(repo.Path, ["kept.txt", "scratch.txt"]);

        Assert.Equal("original\n", repo.Read("kept.txt"));
        Assert.False(repo.Exists("scratch.txt"));
    }

    [Fact]
    public void DiscardingNothingChangesNothing()
    {
        using var repo = RepoWithCommit();
        repo.Write("kept.txt", "modified\n");

        Git.DiscardChanges(repo.Path, []);

        Assert.Equal("modified\n", repo.Read("kept.txt"));
    }

    [Fact]
    public void IgnoringCreatesTheFileWhenThereIsNone()
    {
        using var repo = RepoWithCommit();

        Git.AddToGitignore(repo.Path, "*.glb");

        Assert.Contains("*.glb", repo.Read(".gitignore"));
    }

    [Fact]
    public void IgnoringAppendsWithoutDisturbingWhatIsThere()
    {
        using var repo = RepoWithCommit();
        repo.Write(".gitignore", "bin/\nobj/\n");

        Git.AddToGitignore(repo.Path, "*.glb");

        var lines = repo.Read(".gitignore").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["bin/", "obj/", "*.glb"], lines.Select(l => l.Trim()));
    }

    [Fact]
    public void IgnoringAppendsToAFileWithNoTrailingNewline()
    {
        using var repo = RepoWithCommit();
        repo.Write(".gitignore", "bin/");

        Git.AddToGitignore(repo.Path, "*.glb");

        var lines = repo.Read(".gitignore").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["bin/", "*.glb"], lines.Select(l => l.Trim()));
    }

    [Fact]
    public void IgnoringTheSamePatternTwiceAddsItOnce()
    {
        using var repo = RepoWithCommit();

        Git.AddToGitignore(repo.Path, "*.glb");
        Git.AddToGitignore(repo.Path, "*.glb");

        var lines = repo.Read(".gitignore").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }
}
