using System.Collections.Generic;
using GitGui.HostProviders;
using GitGui.Models;

namespace GitGui.Services;

/// <summary>
/// Everything the UI needs from a local clone. Implementations are synchronous and
/// may block; callers are expected to invoke them off the UI thread.
/// </summary>
public interface IGitService
{
    /// <summary>True if <paramref name="path"/> is inside a git working tree.</summary>
    bool IsRepository(string path);

    /// <summary>Reads repository metadata: name, owner, host, ahead/behind, last fetch.</summary>
    RepositoryInfo OpenRepository(string path);

    IReadOnlyList<BranchInfo> GetBranches(string path);

    /// <summary>Working-tree changes, staged and unstaged and untracked, each with its diff.</summary>
    IReadOnlyList<FileChange> GetWorkingChanges(string path);

    IReadOnlyList<CommitInfo> GetHistory(string path, int maxCount);

    /// <summary>Diffs for one commit against its first parent. Loaded on demand.</summary>
    IReadOnlyList<FileChange> GetCommitFiles(string path, string sha);

    /// <summary>Stages the given paths and commits them. Returns the new commit's sha.</summary>
    string Commit(string path, IEnumerable<string> paths, string summary, string description);

    void CheckoutBranch(string path, string branchName);

    /// <summary>The origin URL, so callers can work out which account to authenticate with.</summary>
    string? GetRemoteUrl(string path);

    /// <summary>Fetches from origin. Returns a short description of what happened.</summary>
    string Fetch(string path, GitCredentials? credentials);

    /// <summary>Fetches and merges the tracked upstream branch.</summary>
    string Pull(string path, GitCredentials? credentials);

    /// <summary>
    /// Pushes the current branch, setting its upstream on first push so the user
    /// doesn't have to run git themselves for a freshly created branch.
    /// </summary>
    string Push(string path, GitCredentials? credentials);
}
