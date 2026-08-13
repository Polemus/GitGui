using System.Collections.Generic;
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
}
