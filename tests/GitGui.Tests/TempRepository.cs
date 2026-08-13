using LibGit2Sharp;

namespace GitGui.Tests;

/// <summary>
/// A throwaway repository on disk. LibGit2Sharp bundles its own native library, so these
/// need no git installation and no network - they are as portable as the pure-function
/// tests, just slower.
/// </summary>
public sealed class TempRepository : IDisposable
{
    public string Path { get; }

    public TempRepository()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gitgui-tests", Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(Path);
        Repository.Init(Path);

        using var repo = new Repository(Path);
        repo.Config.Set("user.name", "Test");
        repo.Config.Set("user.email", "test@example.com");
    }

    public string Write(string relativePath, string contents)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    public string Read(string relativePath)
        => File.ReadAllText(System.IO.Path.Combine(Path, relativePath));

    public bool Exists(string relativePath)
        => File.Exists(System.IO.Path.Combine(Path, relativePath));

    public void Commit(string message)
    {
        using var repo = new Repository(Path);
        Commands.Stage(repo, "*");

        var signature = new Signature("Test", "test@example.com", DateTimeOffset.Now);
        repo.Commit(message, signature, signature);
    }

    public string CurrentBranch()
    {
        using var repo = new Repository(Path);
        return repo.Head.FriendlyName;
    }

    public int StashCount()
    {
        using var repo = new Repository(Path);
        return repo.Stashes.Count();
    }

    public string HeadSha()
    {
        using var repo = new Repository(Path);
        return repo.Head.Tip!.Sha;
    }

    /// <summary>
    /// Commit shas on the current branch, newest first. Sorted the same way
    /// <c>GetHistory</c> sorts: a test makes all its commits within the same second, and
    /// time alone leaves those in an arbitrary order.
    /// </summary>
    public IReadOnlyList<string> Shas()
    {
        using var repo = new Repository(Path);

        return repo.Commits
            .QueryBy(new CommitFilter
            {
                IncludeReachableFrom = repo.Head,
                SortBy = CommitSortStrategies.Time | CommitSortStrategies.Topological,
            })
            .Select(c => c.Sha)
            .ToList();
    }

    /// <summary>
    /// The raw index/worktree status of one path. Soft and mixed resets differ only
    /// here, so the tests have to look at it rather than at our own change list.
    /// </summary>
    public FileStatus StatusOf(string relativePath)
    {
        using var repo = new Repository(Path);
        return repo.RetrieveStatus(relativePath);
    }

    /// <summary>What git thinks it is part-way through, if anything.</summary>
    public CurrentOperation Operation()
    {
        using var repo = new Repository(Path);
        return repo.Info.CurrentOperation;
    }

    /// <summary>Null for a lightweight tag, which carries no message at all.</summary>
    public string? TagMessage(string name)
    {
        using var repo = new Repository(Path);
        return repo.Tags[name]?.Annotation?.Message;
    }

    public void Dispose()
    {
        try
        {
            // Git marks objects read-only, which blocks a plain recursive delete.
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
