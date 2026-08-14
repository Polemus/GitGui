using System;
using System.Collections.Generic;
using Omnigit.HostProviders;
using Omnigit.Models;

namespace Omnigit.Services;

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
    /// <param name="startPoint">
    /// Where a created branch begins, when it should not begin at HEAD. Ignored unless
    /// <paramref name="create"/> is true.
    /// </param>
    /// <returns>
    /// <see cref="SwitchOutcome.Conflicts"/> when a carried file also differs on the
    /// target branch, in which case nothing was changed and the caller has to say so.
    /// </returns>
    SwitchResult SwitchBranch(
        string path, string branchName, bool create, IReadOnlyList<string>? bringPaths,
        string? startPoint = null);

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

    // ---- Acting on a commit that already exists ----------------------------

    /// <summary>
    /// Tags a commit. Lightweight with no <paramref name="message"/>, annotated with one.
    /// Returns the name git actually used.
    /// </summary>
    string CreateTag(string path, string name, string sha, string? message);

    /// <summary>
    /// Checks out a commit directly, leaving HEAD detached. Refuses while the working
    /// tree is dirty, since those changes would follow onto a HEAD no branch points at.
    /// </summary>
    SwitchResult CheckoutCommit(string path, string sha);

    /// <summary>Commits the inverse of a commit. Conflicts come back as a result.</summary>
    CommitOperationResult RevertCommit(string path, string sha);

    /// <summary>
    /// Applies a commit's changes to <paramref name="ontoBranch"/>, switching to that
    /// branch first. Empty means the branch already checked out.
    /// </summary>
    CommitOperationResult CherryPickCommit(string path, string sha, string ontoBranch);

    /// <summary>
    /// Moves the current branch to a commit. <see cref="ResetKind.Hard"/> discards
    /// uncommitted work irrecoverably - the caller confirms before calling.
    /// </summary>
    void ResetToCommit(string path, string sha, ResetKind kind);

    // ---- Finishing what git could not ---------------------------------------

    /// <summary>Paths git left conflicted, from the index rather than the working tree.</summary>
    IReadOnlyList<string> GetConflictedPaths(string path);

    /// <summary>Resolves a conflicted file by keeping one side of it whole.</summary>
    void ResolveConflict(string path, string file, ConflictSide side);

    /// <summary>Accepts conflicted files as they now stand in the working tree.</summary>
    void MarkConflictResolved(string path, IEnumerable<string> files);

    /// <summary>
    /// Abandons the merge, revert or cherry-pick in progress and resets to the last
    /// commit. Uncommitted work goes with it, so the caller confirms first.
    /// </summary>
    void AbortOperation(string path);

    /// <summary>
    /// The message git prepared for the commit that would finish the operation in
    /// progress, or null if there isn't one.
    /// </summary>
    (string Summary, string Description)? GetPendingMessage(string path);

    /// <summary>
    /// Throws away working-tree changes to these paths: tracked files go back to HEAD,
    /// untracked files are deleted. Unrecoverable by design - the caller confirms first.
    /// </summary>
    void DiscardChanges(string path, IEnumerable<string> paths);

    /// <summary>
    /// Appends a pattern to the repository's root <c>.gitignore</c>, creating the file if
    /// it isn't there. A pattern already present is not added twice.
    /// </summary>
    void AddToGitignore(string path, string pattern);

    /// <summary>The working tree's root directory, for resolving a change's full path.</summary>
    string GetWorkingDirectory(string path);

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
