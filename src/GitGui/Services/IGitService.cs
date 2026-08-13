using System;
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

    /// <summary>
    /// Creates a branch at the current HEAD and checks it out. Returns the name actually
    /// used, which git may have normalised.
    /// </summary>
    string CreateBranch(string path, string branchName);

    /// <summary>
    /// Switches branch, deciding what happens to uncommitted work first.
    /// <paramref name="bringPaths"/> lists the files to carry across; everything else
    /// changed is stashed against the branch being left. Null brings everything, which
    /// is what a plain checkout does.
    /// </summary>
    void SwitchBranch(string path, string branchName, bool create, IReadOnlyList<string>? bringPaths);

    /// <summary>Stash entries, newest first, across all branches.</summary>
    IReadOnlyList<StashInfo> GetStashes(string path);

    /// <summary>Restores a stash into the working tree and removes it from the stack.</summary>
    void PopStash(string path, int index);

    /// <summary>Throws the stash away without restoring it.</summary>
    void DropStash(string path, int index);

    /// <summary>
    /// Replaces the last commit with one carrying this message and these paths. Only
    /// valid while the commit has not been pushed, which the caller checks.
    /// </summary>
    string AmendCommit(string path, IEnumerable<string> paths, string summary, string description);

    /// <summary>The last commit's message, so an amend can start from what's there.</summary>
    (string Summary, string Description)? GetLastCommitMessage(string path);

    /// <summary>The origin URL, so callers can work out which account to authenticate with.</summary>
    string? GetRemoteUrl(string path);

    /// <summary>
    /// Clones into <paramref name="targetPath"/>, which must not already hold anything.
    /// Authentication problems come back as a <see cref="SyncResult"/> like the others.
    /// </summary>
    SyncResult Clone(string url, string targetPath, GitCredentials? credentials, Action<string>? trace = null);

    /// <summary>
    /// Fetches from origin. Authentication problems come back as a
    /// <see cref="SyncResult"/> rather than an exception - being signed out is an
    /// ordinary condition, not a fault.
    /// </summary>
    SyncResult Fetch(string path, GitCredentials? credentials, Action<string>? trace = null);

    /// <summary>Fetches and merges the tracked upstream branch.</summary>
    SyncResult Pull(string path, GitCredentials? credentials, Action<string>? trace = null);

    /// <summary>
    /// Pushes the current branch, setting its upstream on first push so the user
    /// doesn't have to run git themselves for a freshly created branch.
    /// </summary>
    SyncResult Push(string path, GitCredentials? credentials, Action<string>? trace = null);
}
