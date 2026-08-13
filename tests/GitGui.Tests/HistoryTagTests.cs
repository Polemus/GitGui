using GitGui.Services;
using LibGit2Sharp;

namespace GitGui.Tests;

/// <summary>
/// Tags are what the history badges are made of. The one that is easy to get wrong is the
/// annotated tag: it points at a tag object rather than the commit, so a naive lookup by
/// target sha finds nothing and the badge silently never appears.
/// </summary>
public class HistoryTagTests
{
    private static readonly Signature Who = new("Test", "test@example.com", DateTimeOffset.Now);

    [Fact]
    public void CarriesALightweightTag()
    {
        using var temp = new TempRepository();
        temp.Write("a.txt", "one");
        temp.Commit("first");
        Tag(temp, "v1.0.0", annotated: false);

        var history = new GitService().GetHistory(temp.Path, 10);

        Assert.Equal(["v1.0.0"], history[0].Tags);
        Assert.True(history[0].HasTags);
    }

    [Fact]
    public void CarriesAnAnnotatedTag()
    {
        using var temp = new TempRepository();
        temp.Write("a.txt", "one");
        temp.Commit("first");
        Tag(temp, "v2.0.0", annotated: true);

        var history = new GitService().GetHistory(temp.Path, 10);

        Assert.Equal(["v2.0.0"], history[0].Tags);
    }

    [Fact]
    public void PutsEveryTagOnItsOwnCommitAndLeavesTheRestBare()
    {
        using var temp = new TempRepository();
        temp.Write("a.txt", "one");
        temp.Commit("first");
        Tag(temp, "v1.0.0", annotated: false);

        temp.Write("a.txt", "two");
        temp.Commit("second");

        var history = new GitService().GetHistory(temp.Path, 10);

        Assert.Equal("second", history[0].Summary);
        Assert.Empty(history[0].Tags);
        Assert.False(history[0].HasTags);
        Assert.Equal(["v1.0.0"], history[1].Tags);
    }

    [Fact]
    public void SortsSeveralTagsOnOneCommit()
    {
        using var temp = new TempRepository();
        temp.Write("a.txt", "one");
        temp.Commit("first");
        Tag(temp, "v1.0.1", annotated: false);
        Tag(temp, "release", annotated: true);
        Tag(temp, "v1.0.0", annotated: false);

        var history = new GitService().GetHistory(temp.Path, 10);

        Assert.Equal(["release", "v1.0.0", "v1.0.1"], history[0].Tags);
    }

    private static void Tag(TempRepository temp, string name, bool annotated)
    {
        using var repo = new Repository(temp.Path);

        if (annotated)
            repo.ApplyTag(name, Who, $"{name} release");
        else
            repo.ApplyTag(name);
    }
}
