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
