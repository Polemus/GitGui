using Omnigit.Models;
using Omnigit.Services;
using Omnigit.ViewModels;

namespace Omnigit.Tests;

/// <summary>
/// Removing a repository from the list, and deleting one from disk.
/// </summary>
/// <remarks>
/// The second is the dangerous one, and the tests are written around the two ways it
/// could hurt someone: deleting something that is on no server without saying so, and
/// deleting outright when the desktop has a trash to put it in.
/// </remarks>
public class RepositoryRemovalTests
{
    private static RepositoryRemovalViewModel Prompt(
        int changes = 0, int ahead = 0, int unpublished = 0, int stashes = 0)
        => new()
        {
            Repository = new RepositoryInfo
            {
                Name = "omnigit",
                LocalPath = "/tmp/omnigit",
                Owner = "Polemus",
                Host = new GitHost
                {
                    Id = "github",
                    Name = "GitHub",
                    Kind = HostKind.GitHub,
                    BaseUrl = "https://github.com",
                    AccentHex = "#3399CC",
                    Badge = "GH",
                },
                DefaultBranch = "main",
            },
            Path = "/tmp/omnigit",
            UncommittedChanges = changes,
            UnpushedCommits = ahead,
            UnpublishedBranches = unpublished,
            Stashes = stashes,
        };

    // ---- What the dialog says ----------------------------------------------

    /// <summary>
    /// A clone that is level with its remote is a copy of something the server still
    /// has. Warning about it every time is how a warning stops being read.
    /// </summary>
    [Fact]
    public void A_clone_that_is_fully_pushed_raises_nothing()
    {
        var prompt = Prompt();

        Assert.False(prompt.HasWarnings);
        Assert.Empty(prompt.Warnings);
        Assert.Contains("the remote still has it", prompt.Summary);
    }

    [Fact]
    public void Uncommitted_work_is_counted_rather_than_alluded_to()
    {
        var prompt = Prompt(changes: 4);

        Assert.True(prompt.HasWarnings);
        Assert.Contains("4 uncommitted changes", prompt.WarningSummary);
        Assert.Contains("none of which are on the remote", prompt.WarningSummary);
    }

    [Fact]
    public void One_of_something_is_not_described_as_one_things()
    {
        Assert.Contains("1 uncommitted change", Prompt(changes: 1).WarningSummary);
        Assert.Contains("1 unpushed commit", Prompt(ahead: 1).WarningSummary);
        Assert.Contains("1 stash", Prompt(stashes: 1).WarningSummary);
        Assert.Contains("2 stashes", Prompt(stashes: 2).WarningSummary);
    }

    [Fact]
    public void Several_warnings_read_as_a_sentence()
    {
        var summary = Prompt(changes: 2, ahead: 3, stashes: 1).WarningSummary;

        Assert.Contains("2 uncommitted changes, 3 unpushed commits and 1 stash", summary);
    }

    [Fact]
    public void Two_warnings_are_joined_with_and_not_a_comma()
    {
        Assert.Contains("2 uncommitted changes and 1 stash", Prompt(changes: 2, stashes: 1).WarningSummary);
    }

    /// <summary>
    /// The button says where the folder goes. "Delete" would overstate what happens to
    /// something that can be dragged back out of the trash.
    /// </summary>
    [Fact]
    public void The_button_names_the_trash()
    {
        Assert.Equal("Move to trash", Prompt().ConfirmLabel);
        Assert.Contains("trash", Prompt().Summary);
    }

    /// <summary>Deleting the wrong folder is the failure here, so the path is shown.</summary>
    [Fact]
    public void The_full_path_is_part_of_the_question()
    {
        Assert.Equal("/tmp/omnigit", Prompt().Path);
        Assert.Contains("omnigit", Prompt().Title);
    }

    // ---- Actually moving it to the trash ------------------------------------

    [Fact]
    public void A_directory_that_is_not_there_is_not_an_error()
    {
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        Assert.Equal(TrashOutcome.NotFound, Trash.MoveDirectory(missing).Outcome);
    }

    /// <summary>
    /// The whole reason the class exists: what it removes has to be recoverable. On
    /// Linux that means the freedesktop trash, and a restore needs both halves - the
    /// files, and a .trashinfo naming where they came from.
    /// </summary>
    [Fact]
    public void A_trashed_directory_can_be_found_and_put_back()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var home = new TemporaryHome();

        var repository = Path.Combine(home.Path, "work", "omnigit");
        Directory.CreateDirectory(repository);
        File.WriteAllText(Path.Combine(repository, "uncommitted.txt"), "never pushed anywhere");

        var result = Trash.MoveDirectory(repository);

        Assert.Equal(TrashOutcome.Trashed, result.Outcome);
        Assert.False(Directory.Exists(repository));

        var trashed = Path.Combine(home.Trash, "files", "omnigit");
        var info = Path.Combine(home.Trash, "info", "omnigit.trashinfo");

        Assert.True(Directory.Exists(trashed), "the files should be in the trash");
        Assert.True(File.Exists(info), "without a .trashinfo the desktop cannot restore it");

        // The work is still there, which is what "recoverable" has to mean.
        Assert.Equal(
            "never pushed anywhere",
            File.ReadAllText(Path.Combine(trashed, "uncommitted.txt")));

        // And the info names where to put it back.
        Assert.Contains($"Path={repository}", File.ReadAllText(info));
    }

    /// <summary>
    /// Two repositories can be called the same thing. The second must not land on top of
    /// the first, or trashing one would destroy the other - inside the very mechanism
    /// that exists to make this recoverable.
    /// </summary>
    [Fact]
    public void Trashing_two_repositories_of_the_same_name_keeps_both()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var home = new TemporaryHome();

        foreach (var owner in new[] { "first", "second" })
        {
            var repository = Path.Combine(home.Path, owner, "omnigit");
            Directory.CreateDirectory(repository);
            File.WriteAllText(Path.Combine(repository, "which.txt"), owner);

            Assert.Equal(TrashOutcome.Trashed, Trash.MoveDirectory(repository).Outcome);
        }

        var files = Directory.GetDirectories(Path.Combine(home.Trash, "files"));
        var infos = Directory.GetFiles(Path.Combine(home.Trash, "info"));

        Assert.Equal(2, files.Length);
        Assert.Equal(2, infos.Length);

        var kept = files.Select(d => File.ReadAllText(Path.Combine(d, "which.txt"))).Order().ToList();
        Assert.Equal(new[] { "first", "second" }, kept);
    }

    /// <summary>
    /// A trash of our own, so the suite never puts anything in the real one and never
    /// depends on what is already in it.
    /// </summary>
    private sealed class TemporaryHome : IDisposable
    {
        private readonly string? _previous;

        public TemporaryHome()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
            Directory.CreateDirectory(Path);

            // gio would use the desktop's trash rather than this one, so it is taken out
            // of the picture: what is under test is the fallback we wrote.
            _previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", System.IO.Path.Combine(Path, "data"));
        }

        public string Path { get; }

        public string Trash => System.IO.Path.Combine(Path, "data", "Trash");

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", _previous);
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
