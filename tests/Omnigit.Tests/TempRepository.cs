using LibGit2Sharp;

namespace Omnigit.Tests;

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
            System.IO.Path.GetTempPath(), "omnigit-tests", Guid.NewGuid().ToString("n"));

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

    /// <summary>
    /// Gives the repository an "origin" pointing at a bare repository next door. A local
    /// path is a transport libgit2 supports, so pushing to it needs no network and no
    /// server - the same thing a real remote does, minus the wire.
    /// </summary>
    public void AddOrigin()
    {
        var bare = System.IO.Path.Combine(Path + "-origin.git");
        Repository.Init(bare, isBare: true);

        using var repo = new Repository(Path);
        repo.Network.Remotes.Add("origin", bare);
    }

    /// <summary>
    /// Pushes the current branch the way <c>git push origin &lt;branch&gt;</c> does -
    /// without <c>-u</c>, so nothing records an upstream. That is the state a repository
    /// ends up in when it was init'ed and pushed rather than cloned.
    /// </summary>
    public void PushWithoutUpstream()
    {
        using var repo = new Repository(Path);
        var branch = repo.Head;

        repo.Network.Push(repo.Network.Remotes["origin"], branch.CanonicalName);

        Assert.False(repo.Branches[branch.FriendlyName].IsTracking,
            "the push was meant to leave the branch untracked");
    }

    /// <summary>
    /// Makes a branch at the current HEAD, pushes it, and deletes the local half - which
    /// leaves <c>refs/remotes/origin/&lt;name&gt;</c> and nothing here, the state a clone
    /// is in for every branch but the one it checked out.
    /// </summary>
    public void AddRemoteOnlyBranch(string name, string fileName, string contents)
    {
        var startingOn = CurrentBranch();

        using (var repo = new Repository(Path))
        {
            Commands.Checkout(repo, repo.CreateBranch(name));
        }

        Write(fileName, contents);
        Commit($"work on {name}");

        using (var repo = new Repository(Path))
        {
            repo.Network.Push(repo.Network.Remotes["origin"], $"refs/heads/{name}");
            Commands.Checkout(repo, repo.Branches[startingOn]);
            repo.Branches.Remove(name);

            Assert.NotNull(repo.Branches[$"origin/{name}"]);
            Assert.Null(repo.Branches[name]);
        }
    }

    /// <summary>
    /// Points a remote-tracking ref at HEAD without any branch behind it. Stands in for
    /// the two refs under <c>refs/remotes/origin</c> that are not branches anyone pushed:
    /// the <c>HEAD</c> symref, and the <c>pr/&lt;n&gt;</c> mirrors our own pull request
    /// fetch writes.
    /// </summary>
    public void AddRemoteRef(string name)
    {
        using var repo = new Repository(Path);
        repo.Refs.Add($"refs/remotes/origin/{name}", repo.Head.Tip!.Sha);
    }

    public string? UpstreamOf(string branchName)
    {
        using var repo = new Repository(Path);
        return repo.Branches[branchName]?.TrackedBranch?.FriendlyName;
    }

    public string TipOf(string branchName)
    {
        using var repo = new Repository(Path);
        return repo.Branches[branchName]!.Tip!.Sha;
    }

    /// <summary>Records branch.&lt;name&gt;.remote/.merge, as clone and push -u do.</summary>
    public void SetUpstream()
    {
        using var repo = new Repository(Path);
        var branch = repo.Head;

        repo.Branches.Update(branch,
            b => b.Remote = "origin",
            b => b.UpstreamBranch = branch.CanonicalName);
    }

    public bool IsTracking()
    {
        using var repo = new Repository(Path);
        return repo.Head.IsTracking;
    }

    /// <summary>Moves the branch back, leaving the remote-tracking ref where it was.</summary>
    public void ResetHardTo(string sha)
    {
        using var repo = new Repository(Path);
        repo.Reset(ResetMode.Hard, repo.Lookup<Commit>(sha));
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
        // The bare origin from AddOrigin sits beside the working copy, so it needs
        // removing too or every test that pushes leaves one behind.
        Remove(Path);
        Remove(Path + "-origin.git");
    }

    private static void Remove(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;

            // Git marks objects read-only, which blocks a plain recursive delete.
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
