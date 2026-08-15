using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Omnigit.HostProviders;
using Omnigit.Models;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace Omnigit.Services;

/// <summary>
/// <see cref="IGitService"/> backed by libgit2. The native library ships inside the
/// LibGit2Sharp package and is copied into our self-contained publish, so end users
/// need neither git nor a runtime installed.
/// </summary>
/// <remarks>
/// A <see cref="Repository"/> handle is opened and disposed per call rather than
/// cached. libgit2 handles are not thread-safe, and the UI hits these methods from
/// pooled background threads; opening per call is cheap next to the work each one
/// does and removes the need for locking.
/// </remarks>
public sealed partial class GitService : IGitService
{
    /// <summary>How much of a file we read before deciding it is binary.</summary>
    private const int BinarySniffBytes = 8000;

    /// <summary>Untracked files above this size are listed without a rendered diff.</summary>
    private const long MaxUntrackedDiffBytes = 512 * 1024;

    public bool IsRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        return Repository.Discover(path) is not null;
    }

    public RepositoryInfo OpenRepository(string path)
    {
        using var repo = new Repository(Discover(path));

        var workdir = repo.Info.WorkingDirectory?.TrimEnd(Path.DirectorySeparatorChar, '/')
                      ?? path;

        var origin = repo.Network.Remotes["origin"] ?? repo.Network.Remotes.FirstOrDefault();
        var identity = HostResolver.Parse(origin?.Url);

        var head = repo.Head;
        var standing = Standing(repo, head);

        return new RepositoryInfo
        {
            Name = identity?.Name ?? new DirectoryInfo(workdir).Name,
            Owner = identity?.Owner ?? string.Empty,
            Host = identity?.Host ?? HostResolver.LocalOnly,
            LocalPath = workdir,
            DefaultBranch = DefaultBranchName(repo, origin),
            IsPrivate = null, // Not knowable locally; the host API fills this in later.
            Ahead = standing.Ahead,
            Behind = standing.Behind,
            HasRemote = standing.HasRemote,
            IsPublished = standing.IsPublished,
            LastFetched = LastFetchTime(repo.Info.Path),
            IsDetached = repo.Info.IsHeadDetached,
            HeadSha = head?.Tip?.Sha ?? string.Empty,
            Operation = ToOperation(repo.Info.CurrentOperation),
            ConflictCount = repo.Index.Conflicts.Count(),
        };
    }

    /// <summary>
    /// The branch this repository's work merges back into.
    /// </summary>
    /// <remarks>
    /// This used to be the branch currently checked out, which is not a default branch
    /// but the opposite of the question - it made "open a pull request from here into the
    /// default" a proposal to merge a branch into itself, so the button was disabled on
    /// every repository and never said why.
    ///
    /// <c>refs/remotes/&lt;remote&gt;/HEAD</c> is the local record of what the server calls
    /// default, written by <c>git clone</c>. A repository that was init'ed and pushed has
    /// no such ref - the same gap <c>EnsureTracking</c> exists for - so the usual names
    /// are tried next, on the remote before locally: what the forge has is the answer,
    /// and a local <c>main</c> next to a remote <c>master</c> is the case that decides it.
    /// </remarks>
    private static string DefaultBranchName(Repository repo, Remote? remote)
    {
        if (remote is not null
            && repo.Refs[$"refs/remotes/{remote.Name}/HEAD"] is SymbolicReference symbolic)
        {
            var prefix = $"refs/remotes/{remote.Name}/";
            var target = symbolic.Target?.CanonicalName ?? symbolic.TargetIdentifier;

            if (target?.StartsWith(prefix, StringComparison.Ordinal) == true)
                return target[prefix.Length..];
        }

        string[] usual = ["main", "master", "trunk", "develop"];

        if (remote is not null)
        {
            foreach (var name in usual)
            {
                if (repo.Refs[$"refs/remotes/{remote.Name}/{name}"] is not null)
                    return name;
            }
        }

        foreach (var name in usual)
        {
            if (repo.Branches[name] is not null)
                return name;
        }

        return repo.Head?.FriendlyName ?? "HEAD";
    }

    public IReadOnlyList<BranchInfo> GetBranches(string path)
    {
        using var repo = new Repository(Discover(path));

        var currentName = repo.Head?.FriendlyName;

        // The same answer the repository header uses, rather than a second guess at it:
        // a project whose trunk is called something else has one default branch, not one
        // here and a different one there.
        var defaultName = DefaultBranchName(repo, repo.Network.Remotes["origin"]
                                                  ?? repo.Network.Remotes.FirstOrDefault());

        return repo.Branches
            .Where(b => !b.IsRemote)
            .Select(b => new BranchInfo
            {
                Name = b.FriendlyName,
                LastCommitSummary = b.Tip?.MessageShort ?? string.Empty,
                LastCommitAt = b.Tip?.Committer.When ?? DateTimeOffset.MinValue,
                IsCurrent = b.FriendlyName == currentName,
                IsDefault = b.FriendlyName == defaultName,
            })
            .OrderByDescending(b => b.IsCurrent)
            .ThenByDescending(b => b.LastCommitAt)
            .ToList();
    }

    public IReadOnlyList<FileChange> GetWorkingChanges(string path)
    {
        using var repo = new Repository(Discover(path));

        // Rename detection is deliberately off. It pairs a delete with a similar-enough
        // add into one entry carrying only the *new* path, so moving a file listed one
        // row, the commit staged that one path, and the deletion of the old path was
        // left behind in the working tree - silently, since it never appeared in the
        // list to begin with. The patch below does no rename detection either, so a
        // paired entry was rendered as a plain "Added" anyway: the collapsing cost us
        // the deletion and bought nothing.
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
        });

        var entries = status
            .Where(e => e.State != FileStatus.Unaltered && e.State != FileStatus.Ignored)
            .ToList();

        if (entries.Count == 0)
            return [];

        // Untracked files have no blob to diff against, so libgit2 won't produce a
        // patch for them. They're rendered from file contents instead, below.
        var untracked = entries
            .Where(e => e.State.HasFlag(FileStatus.NewInWorkdir))
            .Select(e => e.FilePath)
            .ToHashSet(StringComparer.Ordinal);

        var tracked = entries
            .Select(e => e.FilePath)
            .Where(p => !untracked.Contains(p))
            .ToList();

        var patches = new Dictionary<string, PatchEntryChanges>(StringComparer.Ordinal);

        if (tracked.Count > 0 && repo.Head?.Tip is { } tip)
        {
            var patch = repo.Diff.Compare<Patch>(
                tip.Tree,
                DiffTargets.WorkingDirectory | DiffTargets.Index,
                tracked,
                new ExplicitPathsOptions { ShouldFailOnUnmatchedPath = false });

            foreach (var entry in patch)
                patches[entry.Path] = entry;
        }

        var workdir = repo.Info.WorkingDirectory ?? path;
        var changes = new List<FileChange>(entries.Count);

        foreach (var entry in entries.OrderBy(e => e.FilePath, StringComparer.Ordinal))
        {
            if (patches.TryGetValue(entry.FilePath, out var pec))
            {
                changes.Add(new FileChange
                {
                    Path = pec.Path,
                    Status = ToChangeStatus(pec.Status, entry.State),
                    Additions = pec.LinesAdded,
                    Deletions = pec.LinesDeleted,
                    Diff = UnifiedDiffParser.Parse(pec.Patch),
                });
            }
            else
            {
                changes.Add(DescribeUntracked(workdir, entry));
            }
        }

        return changes;
    }

    public IReadOnlyList<CommitInfo> GetHistory(string path, int maxCount)
    {
        using var repo = new Repository(Discover(path));

        if (repo.Head?.Tip is null)
            return [];

        var tags = TagsBySha(repo);

        return repo.Commits
            .QueryBy(new CommitFilter
            {
                IncludeReachableFrom = repo.Head,
                // Time alone ties commits made within the same second into an
                // arbitrary order; topological breaks those ties by ancestry.
                SortBy = CommitSortStrategies.Time | CommitSortStrategies.Topological,
            })
            .Take(maxCount)
            .Select(c => new CommitInfo
            {
                Sha = c.Sha,
                Summary = string.IsNullOrWhiteSpace(c.MessageShort) ? "(no message)" : c.MessageShort,
                AuthorName = c.Author.Name,
                AuthorInitials = Initials(c.Author.Name),
                AvatarHex = AvatarColour(c.Author.Email ?? c.Author.Name),
                CommittedAt = c.Author.When,
                // Counting changed files per commit means diffing every one of them,
                // which is far too slow for a list. It's filled in on selection.
                FilesChanged = 0,
                Tags = tags.TryGetValue(c.Sha, out var names) ? names : [],
            })
            .ToList();
    }

    /// <summary>
    /// Every tag, grouped by the commit it ends up on. Built once per history load rather
    /// than looked up per commit: a repository has few tags and many commits, and this is
    /// the only shape that avoids walking the tag list once for every row.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> TagsBySha(Repository repo)
    {
        var byCommit = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var tag in repo.Tags)
        {
            // An annotated tag points at a tag object, not the commit, so it has to be
            // peeled first. Lightweight tags already point straight at the commit.
            if ((tag.PeeledTarget ?? tag.Target) is not Commit commit)
                continue;

            if (!byCommit.TryGetValue(commit.Sha, out var names))
                byCommit[commit.Sha] = names = [];

            names.Add(tag.FriendlyName);
        }

        return byCommit.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                pair.Value.Sort(StringComparer.OrdinalIgnoreCase);
                return (IReadOnlyList<string>)pair.Value;
            },
            StringComparer.Ordinal);
    }

    public IReadOnlyList<FileChange> GetCommitFiles(string path, string sha)
    {
        using var repo = new Repository(Discover(path));

        if (repo.Lookup<Commit>(sha) is not { } commit)
            return [];

        // Root commits have no parent, so they diff against an empty tree.
        var parentTree = commit.Parents.FirstOrDefault()?.Tree;

        var patch = repo.Diff.Compare<Patch>(parentTree, commit.Tree);

        return patch
            .Select(pec => new FileChange
            {
                Path = pec.Path,
                Status = ToChangeStatus(pec.Status, null),
                Additions = pec.LinesAdded,
                Deletions = pec.LinesDeleted,
                Diff = UnifiedDiffParser.Parse(pec.Patch),
            })
            .ToList();
    }

    public string Commit(string path, IEnumerable<string> paths, string summary, string description)
    {
        var staged = paths.ToList();
        if (staged.Count == 0)
            throw new InvalidOperationException("Nothing selected to commit.");

        if (string.IsNullOrWhiteSpace(summary))
            throw new InvalidOperationException("A commit summary is required.");

        using var repo = new Repository(Discover(path));

        // Stage handles additions, modifications and deletions alike.
        Commands.Stage(repo, staged);

        var message = string.IsNullOrWhiteSpace(description)
            ? summary.Trim()
            : $"{summary.Trim()}\n\n{description.Trim()}";

        var signature = repo.Config.BuildSignature(DateTimeOffset.Now)
                        ?? new Signature("Omnigit", "omnigit@localhost", DateTimeOffset.Now);

        return repo.Commit(message, signature, signature).Sha;
    }

    public void CheckoutBranch(string path, string branchName)
    {
        using var repo = new Repository(Discover(path));

        var branch = repo.Branches[branchName]
                     ?? throw new InvalidOperationException($"Branch '{branchName}' not found.");

        Commands.Checkout(repo, branch);
    }

    public string CreateBranch(string path, string branchName)
    {
        var name = branchName.Trim();

        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException("A branch needs a name.");

        using var repo = new Repository(Discover(path));

        // An empty repository has no HEAD commit to branch from.
        if (repo.Head.Tip is null)
            throw new InvalidOperationException("Commit something before creating a branch.");

        if (repo.Branches[name] is not null)
            throw new InvalidOperationException($"Branch '{name}' already exists.");

        var branch = repo.CreateBranch(name);
        Commands.Checkout(repo, branch);

        return branch.FriendlyName;
    }

    /// <summary>
    /// Switching branches with uncommitted work. Git's own behaviour is to carry
    /// everything across, so "bring all" is a plain checkout and needs no stash at all.
    /// </summary>
    /// <remarks>
    /// Bringing only *some* files is the awkward case: libgit2 has no way to stash a
    /// subset. It is done with two stashes instead. Everything is stashed first, so at no
    /// point does uncommitted work exist only in the working tree - if any later step
    /// fails, the changes are still recoverable from the stack.
    /// </remarks>
    public SwitchResult SwitchBranch(
        string path, string branchName, bool create, IReadOnlyList<string>? bringPaths,
        string? startPoint = null)
    {
        using var repo = new Repository(Discover(path));

        var changed = ChangedPaths(repo);

        var bring = bringPaths is null
            ? changed.ToHashSet(StringComparer.Ordinal)
            : bringPaths.ToHashSet(StringComparer.Ordinal);

        // Checked before anything is stashed or reverted, so a refusal leaves the working
        // tree exactly as it was. Only the carried files can conflict - whatever is left
        // behind is stashed first, which makes it clean by the time checkout runs.
        if (Conflicting(repo, bring, branchName, create, startPoint) is { Count: > 0 } conflicts)
        {
            var names = string.Join(", ", conflicts.Take(3))
                        + (conflicts.Count > 3 ? $" and {conflicts.Count - 3} more" : string.Empty);

            return new SwitchResult(
                SwitchOutcome.Conflicts,
                $"{names} changed on both branches, so bringing it across would overwrite "
                + $"work on {branchName}. Leave it behind to stash it instead.",
                conflicts);
        }

        // Nothing uncommitted, or everything is coming along: git already does this.
        if (changed.Count == 0 || bringPaths is null)
        {
            Switch(repo, branchName, create, startPoint);
            return SwitchResult.Ok();
        }

        var leave = changed.Where(p => !bring.Contains(p)).ToList();

        if (leave.Count == 0)
        {
            Switch(repo, branchName, create, startPoint);
            return SwitchResult.Ok();
        }

        var signature = repo.Config.BuildSignature(DateTimeOffset.Now)
                        ?? new Signature("Omnigit", "omnigit@localhost", DateTimeOffset.Now);

        var from = repo.Head.FriendlyName;

        // Nothing is coming across, so one stash is enough.
        if (bring.Count == 0 || changed.All(p => !bring.Contains(p)))
        {
            repo.Stashes.Add(signature, $"Omnigit: left behind when switching from {from}",
                StashModifiers.IncludeUntracked);

            Switch(repo, branchName, create, startPoint);
            return SwitchResult.Ok();
        }

        // Everything, so the work is safe on the stack from here on.
        repo.Stashes.Add(signature, $"Omnigit: switching from {from}", StashModifiers.IncludeUntracked);

        // Restore it, strip out what is being carried, and stash the remainder. That
        // second stash is the one the user gets back when they return to this branch.
        repo.Stashes.Apply(0);
        RevertPaths(repo, bring);
        repo.Stashes.Add(signature, $"Omnigit: left behind when switching from {from}",
            StashModifiers.IncludeUntracked);

        // The full stash has shifted to 1. Restore it, strip out what is being left, and
        // drop it - leaving only the carried files in the tree for checkout to move.
        repo.Stashes.Apply(1);
        RevertPaths(repo, leave);
        repo.Stashes.Remove(1);

        Switch(repo, branchName, create, startPoint);
        return SwitchResult.Ok();
    }

    /// <summary>
    /// Which of <paramref name="paths"/> git would refuse to carry across: those whose
    /// committed contents differ between HEAD and wherever we are about to end up.
    /// Compared by blob id rather than by attempting the checkout, so the answer names
    /// the files and costs nothing when there is no problem.
    /// </summary>
    private static List<string> Conflicting(
        Repository repo, IEnumerable<string> paths, string branchName, bool create, string? startPoint)
    {
        if (repo.Head.Tip is not { } head)
            return [];

        // A branch created here starts at HEAD unless it was asked for somewhere else, so
        // only a start point can make a created branch differ from what is in the tree.
        var target = create
            ? startPoint is null ? null : repo.Lookup<Commit>(startPoint)
            : repo.Branches[branchName]?.Tip;

        if (target is null)
            return [];

        return paths
            .Where(p => head[p]?.Target.Id != target[p]?.Target.Id)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static void Switch(Repository repo, string branchName, bool create, string? startPoint)
    {
        if (create)
        {
            if (repo.Head.Tip is null)
                throw new InvalidOperationException("Commit something before creating a branch.");

            if (repo.Branches[branchName] is not null)
                throw new InvalidOperationException($"Branch '{branchName}' already exists.");

            // Without a start point this is "git checkout -b": branch from where we are.
            var created = startPoint is null
                ? repo.CreateBranch(branchName)
                : repo.CreateBranch(branchName, Require(repo, startPoint));

            Commands.Checkout(repo, created);
            return;
        }

        var branch = repo.Branches[branchName]
                     ?? throw new InvalidOperationException($"Branch '{branchName}' not found.");

        Commands.Checkout(repo, branch);
    }

    /// <summary>Every path with uncommitted work, tracked or not.</summary>
    private static List<string> ChangedPaths(Repository repo)
        => repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true })
            .Where(e => e.State != FileStatus.Unaltered && e.State != FileStatus.Ignored)
            .Select(e => e.FilePath)
            .ToList();

    /// <summary>
    /// Undoes the working-tree changes for these paths. Untracked files have no committed
    /// version to check out, so they are deleted instead.
    /// </summary>
    private static void RevertPaths(Repository repo, IEnumerable<string> paths)
    {
        var tracked = new List<string>();

        foreach (var file in paths)
        {
            if (repo.RetrieveStatus(file).HasFlag(FileStatus.NewInWorkdir))
            {
                var full = Path.Combine(repo.Info.WorkingDirectory, file);

                if (File.Exists(full))
                    File.Delete(full);

                continue;
            }

            tracked.Add(file);
        }

        if (tracked.Count == 0 || repo.Head.Tip is null)
            return;

        repo.CheckoutPaths(repo.Head.Tip.Sha, tracked, new CheckoutOptions
        {
            CheckoutModifiers = CheckoutModifiers.Force,
        });
    }

    public IReadOnlyList<StashInfo> GetStashes(string path)
    {
        using var repo = new Repository(Discover(path));

        return repo.Stashes.Select((stash, index) => new StashInfo
        {
            Index = index,
            Message = stash.Message ?? string.Empty,
            BranchName = BranchFromStashMessage(stash.Message),
            CreatedAt = stash.WorkTree?.Committer?.When ?? DateTimeOffset.Now,
        }).ToList();
    }

    /// <summary>
    /// Git writes "WIP on main: 1234567 summary" or "On main: message". The branch is
    /// only recorded there, so it is read back out rather than stored separately.
    /// </summary>
    private static string BranchFromStashMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;

        var text = message.StartsWith("WIP on ", StringComparison.Ordinal) ? message[7..]
            : message.StartsWith("On ", StringComparison.Ordinal) ? message[3..]
            : null;

        if (text is null)
            return string.Empty;

        var colon = text.IndexOf(':');
        return colon < 0 ? text.Trim() : text[..colon].Trim();
    }

    public void PopStash(string path, int index)
    {
        using var repo = new Repository(Discover(path));

        var status = repo.Stashes.Pop(index);

        if (status == StashApplyStatus.Conflicts)
            throw new InvalidOperationException("Restoring the stash caused conflicts — resolve them before continuing.");

        if (status == StashApplyStatus.UncommittedChanges)
            throw new InvalidOperationException("Commit or revert your current changes before restoring the stash.");

        if (status == StashApplyStatus.NotFound)
            throw new InvalidOperationException("That stash no longer exists.");
    }

    public void DropStash(string path, int index)
    {
        using var repo = new Repository(Discover(path));
        repo.Stashes.Remove(index);
    }

    public string AmendCommit(string path, IEnumerable<string> paths, string summary, string description)
    {
        if (string.IsNullOrWhiteSpace(summary))
            throw new InvalidOperationException("A commit summary is required.");

        using var repo = new Repository(Discover(path));

        if (repo.Head.Tip is null)
            throw new InvalidOperationException("There is no commit to amend.");

        var staged = paths.ToList();
        if (staged.Count > 0)
            Commands.Stage(repo, staged);

        var message = string.IsNullOrWhiteSpace(description)
            ? summary.Trim()
            : $"{summary.Trim()}\n\n{description.Trim()}";

        // The original author is kept; only the committer becomes whoever is amending,
        // which is what git itself does.
        var committer = repo.Config.BuildSignature(DateTimeOffset.Now)
                        ?? new Signature("Omnigit", "omnigit@localhost", DateTimeOffset.Now);

        return repo.Commit(message, repo.Head.Tip.Author, committer, new CommitOptions { AmendPreviousCommit = true }).Sha;
    }

    public (string Summary, string Description)? GetLastCommitMessage(string path)
    {
        using var repo = new Repository(Discover(path));

        if (repo.Head.Tip is not { } tip)
            return null;

        var message = tip.Message ?? string.Empty;
        var split = message.IndexOf('\n');

        return split < 0
            ? (message.Trim(), string.Empty)
            : (message[..split].Trim(), message[(split + 1)..].Trim());
    }

    // ----------------------------------------------------- discarding, ignoring

    public string GetWorkingDirectory(string path)
    {
        using var repo = new Repository(Discover(path));
        return repo.Info.WorkingDirectory;
    }

    public void DiscardChanges(string path, IEnumerable<string> paths)
    {
        var wanted = paths.ToList();
        if (wanted.Count == 0)
            return;

        using var repo = new Repository(Discover(path));

        // Untracked files have nothing in HEAD to check out over, so a checkout would
        // silently leave them behind. They have to be deleted outright.
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
        });

        var untracked = status
            .Where(e => e.State.HasFlag(FileStatus.NewInWorkdir))
            .Select(e => e.FilePath)
            .ToHashSet(StringComparer.Ordinal);

        var tracked = wanted.Where(p => !untracked.Contains(p)).ToList();

        if (tracked.Count > 0)
        {
            repo.CheckoutPaths(
                repo.Head.FriendlyName,
                tracked,
                new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
        }

        foreach (var relative in wanted.Where(untracked.Contains))
        {
            var full = Path.Combine(repo.Info.WorkingDirectory, relative);
            if (File.Exists(full))
                File.Delete(full);
        }
    }

    public void AddToGitignore(string path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        using var repo = new Repository(Discover(path));
        var file = Path.Combine(repo.Info.WorkingDirectory, ".gitignore");

        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];

        if (lines.Any(l => l.Trim() == pattern))
            return;

        // ReadAllLines/WriteAllLines round-trips through whole lines, so a file whose
        // last line had no trailing newline gets one rather than being appended to.
        lines.Add(pattern);
        File.WriteAllLines(file, lines);
    }

    // ------------------------------------------------------------- networking

    public string? GetRemoteUrl(string path)
    {
        using var repo = new Repository(Discover(path));
        return (repo.Network.Remotes["origin"] ?? repo.Network.Remotes.FirstOrDefault())?.Url;
    }

    public SyncResult Clone(string url, string targetPath, GitCredentials? credentials, Action<string>? trace = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new SyncResult(SyncOutcome.Failed, "A clone URL is required.");

        // Refusing up front beats letting libgit2 half-write into someone's folder.
        if (Directory.Exists(targetPath) && Directory.EnumerateFileSystemEntries(targetPath).Any())
            return new SyncResult(SyncOutcome.Failed, $"{targetPath} already exists and isn't empty.");

        var probe = new AuthProbe
        {
            Host = HostResolver.Parse(url)?.Host.Id ?? url,
            HadCredentials = credentials is not null,
        };

        trace?.Invoke($"Cloning {url} into {targetPath}");

        var options = new CloneOptions(BuildFetchOptions(credentials, probe, trace));

        if (RunNetwork(probe, () => Repository.Clone(url, targetPath, options)) is { } failure)
        {
            // A failed clone leaves a partial directory behind, which would then block
            // a retry with "already exists and isn't empty".
            TryRemove(targetPath);
            return failure;
        }

        return SyncResult.Ok($"Cloned into {targetPath}");
    }

    /// <summary>Best-effort cleanup of a half-written clone; failing to tidy is not an error.</summary>
    private static void TryRemove(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The user can delete it themselves; saying so twice helps nobody.
        }
    }

    public SyncResult Fetch(string path, GitCredentials? credentials, Action<string>? trace = null)
    {
        using var repo = new Repository(Discover(path));

        if (FindRemote(repo) is not { } remote)
            return NoRemote("fetch from");

        var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification).ToList();
        var probe = new AuthProbe { Host = HostOf(remote), HadCredentials = credentials is not null };

        trace?.Invoke($"Fetching {remote.Name} from {remote.Url}");

        if (RunNetwork(probe, () => Commands.Fetch(
                repo, remote.Name, refSpecs,
                BuildFetchOptions(credentials, probe, trace), "fetch by Omnigit")) is { } failure)
        {
            return failure;
        }

        // Not TrackingDetails: a branch with no upstream would report zero and the fetch
        // would claim to be up to date over commits it had just brought down.
        var behind = Standing(repo, repo.Head).Behind;

        return SyncResult.Ok(behind > 0
            ? $"Fetched from {remote.Name} — {behind} commit{(behind == 1 ? "" : "s")} to pull"
            : $"Fetched from {remote.Name} — already up to date");
    }

    /// <summary>
    /// Brings a pull request's head down into <c>refs/remotes/&lt;remote&gt;/pr/&lt;n&gt;</c>
    /// and reports what the caller has to do to get onto it.
    /// </summary>
    /// <remarks>
    /// Fetched into a remote-tracking ref rather than straight into a branch on purpose.
    /// A branch may be the one checked out, and moving that ref under the working tree
    /// leaves the two disagreeing about what is committed - git's own fetch refuses for
    /// exactly this reason, while libgit2 will do as it is told.
    ///
    /// The local branch is <c>pr/&lt;n&gt;</c>, never the source branch's own name: a pull
    /// request from a fork can be called anything, up to and including the name of a
    /// branch already here with work on it.
    /// </remarks>
    public PullRequestFetch FetchPullRequest(
        string path, int number, string? refSpecTemplate, GitCredentials? credentials, Action<string>? trace = null)
    {
        using var repo = new Repository(Discover(path));

        var local = $"pr/{number}";

        if (FindRemote(repo) is not { } remote)
            return new PullRequestFetch(NoRemote("fetch a pull request from"), local, false, false);

        var head = WebLinks.PullRequestRef(number, refSpecTemplate);
        var mirror = $"refs/remotes/{remote.Name}/pr/{number}";
        var probe = new AuthProbe { Host = HostOf(remote), HadCredentials = credentials is not null };

        trace?.Invoke($"Fetching {head} from {remote.Url}");

        if (RunNetwork(probe, () => Commands.Fetch(
                repo, remote.Name, [$"+{head}:{mirror}"],
                // Prune would delete every other remote-tracking ref: the refspec names
                // one ref, so from its point of view the rest of the remote is gone.
                BuildFetchOptions(credentials, probe, trace, prune: false),
                $"fetch pull request {number} by Omnigit")) is { } failure)
        {
            return new PullRequestFetch(failure, local, false, false);
        }

        if (repo.Refs[mirror]?.ResolveToDirectReference()?.Target is not Commit fetched)
        {
            return new PullRequestFetch(
                new SyncResult(SyncOutcome.Failed,
                    $"The remote has no {head} — the pull request may have been merged or closed."),
                local, false, false);
        }

        var branch = repo.Branches[local];

        if (branch is null)
            return new PullRequestFetch(SyncResult.Ok($"Fetched pull request #{number}"), local, IsNew: true, IsStale: false);

        // Already here from a previous checkout. Fast-forwarding it is safe only while
        // it is strictly behind what was just fetched; anything else means the pull
        // request was force-pushed or the branch has local commits, and moving it would
        // throw one of the two away.
        var behind = branch.Tip is { } tip
                     && repo.ObjectDatabase.FindMergeBase(tip, fetched)?.Sha == tip.Sha
                     && tip.Sha != fetched.Sha;

        if (!behind)
        {
            return new PullRequestFetch(
                SyncResult.Ok($"Fetched pull request #{number}"), local,
                IsNew: false, IsStale: branch.Tip?.Sha != fetched.Sha);
        }

        // Moving a ref nothing is standing on is just a ref write. Moving the one HEAD
        // points at has to take the working tree with it, so that only happens with
        // nothing uncommitted in the way - a fast-forward is not worth risking work over.
        if (branch.IsCurrentRepositoryHead)
        {
            if (ChangedPaths(repo).Count > 0)
            {
                return new PullRequestFetch(
                    SyncResult.Ok($"Fetched pull request #{number}"),
                    local, IsNew: false, IsStale: true);
            }

            Commands.Checkout(repo, fetched);
        }

        repo.Refs.UpdateTarget(repo.Refs[branch.CanonicalName], fetched.Id);

        if (branch.IsCurrentRepositoryHead)
            repo.Refs.UpdateTarget("HEAD", branch.CanonicalName);

        return new PullRequestFetch(
            SyncResult.Ok($"Updated {local} to the latest on pull request #{number}"),
            local, IsNew: false, IsStale: false);
    }

    public SyncResult Pull(string path, GitCredentials? credentials, Action<string>? trace = null)
    {
        using var repo = new Repository(Discover(path));

        var signature = repo.Config.BuildSignature(DateTimeOffset.Now)
                        ?? new Signature("Omnigit", "omnigit@localhost", DateTimeOffset.Now);

        var remote = FindRemote(repo);
        if (remote is null)
            return NoRemote("pull from");

        // Commands.Pull needs an upstream to merge from. A branch already on the remote
        // may still have none - see EnsureTracking - and the remote-tracking ref is the
        // proof that this is the same branch rather than a guess at one.
        if (repo.Head is { } head && Mirror(repo, remote, head) is not null)
            EnsureTracking(repo, head, remote);

        var probe = new AuthProbe { Host = HostOf(remote), HadCredentials = credentials is not null };
        MergeResult? result = null;

        if (RunNetwork(probe, () => result = Commands.Pull(repo, signature, new PullOptions
            {
                FetchOptions = BuildFetchOptions(credentials, probe, trace),
                MergeOptions = new MergeOptions { FailOnConflict = false },
            })) is { } failure)
        {
            return failure;
        }

        return result?.Status switch
        {
            MergeStatus.UpToDate => SyncResult.Ok("Already up to date"),
            MergeStatus.FastForward => SyncResult.Ok($"Fast-forwarded to {Short(result.Commit)}"),
            MergeStatus.NonFastForward => SyncResult.Ok($"Merged to {Short(result.Commit)}"),
            MergeStatus.Conflicts => new SyncResult(SyncOutcome.Failed,
                "Pulled with conflicts — resolve them before committing"),
            _ => SyncResult.Ok("Pull finished"),
        };
    }

    public SyncResult Push(string path, GitCredentials? credentials, Action<string>? trace = null)
    {
        using var repo = new Repository(Discover(path));

        var branch = repo.Head;
        if (branch is null)
            return new SyncResult(SyncOutcome.Failed, "No branch is checked out.");

        if (FindRemote(repo) is not { } remote)
            return NoRemote("push to");

        // Covers both a branch created locally and one pushed without -u.
        branch = EnsureTracking(repo, branch, remote);

        var probe = new AuthProbe { Host = HostOf(remote), HadCredentials = credentials is not null };
        var pushed = 0;

        trace?.Invoke($"Pushing {branch.FriendlyName} to {remote.Url}");
        string? rejection = null;

        var options = new PushOptions
        {
            CredentialsProvider = CredentialsFor(credentials, probe),
            // Recorded rather than thrown, for the same reason as the auth failures.
            OnPushStatusError = error => rejection = $"{remote.Name} rejected {error.Reference}: {error.Message}",
            OnPackBuilderProgress = (_, current, _) => { pushed = current; return true; },
            OnPushTransferProgress = (current, total, _) =>
            {
                if (trace is not null && total > 0 && (current == total || current % 50 == 0))
                    trace($"  {current}/{total} objects sent");

                return true;
            },
        };

        if (RunNetwork(probe, () => repo.Network.Push(branch, options)) is { } failure)
            return failure;

        if (rejection is not null)
            return new SyncResult(SyncOutcome.Failed, rejection);

        return SyncResult.Ok($"Pushed {branch.FriendlyName} to {remote.Name}"
                             + (pushed > 0 ? $" ({pushed} objects)" : string.Empty));
    }

    /// <summary>
    /// Records what libgit2 asked for during one network call, so the failure can be
    /// explained afterwards instead of from inside the callback.
    /// </summary>
    private sealed class AuthProbe
    {
        public bool WasAsked { get; set; }
        public bool HadCredentials { get; init; }
        public required string Host { get; init; }
    }

    /// <param name="prune">
    /// False when the fetch names a single ref rather than the remote's own refspecs:
    /// pruning against one ref would delete every remote-tracking branch, since from
    /// that refspec's point of view none of them are on the remote any more.
    /// </param>
    private static FetchOptions BuildFetchOptions(
        GitCredentials? credentials, AuthProbe probe, Action<string>? trace = null, bool prune = true)
    {
        // libgit2 reports transfer progress per object, which would flood the console.
        // Only the crossing of each 10% boundary is reported.
        var lastReported = -1;

        return new FetchOptions
        {
            CredentialsProvider = CredentialsFor(credentials, probe),
            TagFetchMode = TagFetchMode.Auto,
            Prune = prune,
            OnTransferProgress = progress =>
            {
                if (trace is null || progress.TotalObjects == 0)
                    return true;

                var percent = progress.ReceivedObjects * 100 / progress.TotalObjects;
                if (percent / 10 > lastReported / 10 || percent == 100)
                {
                    lastReported = percent;
                    trace($"  {percent}% — {progress.ReceivedObjects}/{progress.TotalObjects} objects, "
                          + $"{progress.ReceivedBytes / 1024} KB");
                }

                return true;
            },
        };
    }

    /// <summary>
    /// Supplies credentials to libgit2.
    /// </summary>
    /// <remarks>
    /// A handler is always installed, even with no account. libgit2 only invokes it when
    /// the server actually demands authentication, which gives exactly the behaviour we
    /// want: a public remote never calls it and keeps working signed-out, while a private
    /// one calls it and gets a message naming the host. Returning null here instead - the
    /// original mistake - made libgit2 fail with "remote authentication required but no
    /// callback set", which tells the user nothing about what to do.
    /// </remarks>
    private static CredentialsHandler CredentialsFor(GitCredentials? credentials, AuthProbe probe)
    {
        return (_, _, types) =>
        {
            // Only note that authentication was demanded. Throwing here would have to
            // travel back out through native libgit2 frames, which the debugger reports
            // as an unhandled exception and breaks on every time. The failure is
            // explained in RunNetwork instead, once we are back in managed code.
            probe.WasAsked = true;

            if (credentials is null)
                return new DefaultCredentials();

            return types.HasFlag(SupportedCredentialTypes.UsernamePassword)
                ? new UsernamePasswordCredentials
                {
                    Username = credentials.Username,
                    Password = credentials.Password,
                }
                : new DefaultCredentials();
        };
    }

    /// <summary>Domain of a remote, for use in messages.</summary>
    private static string HostOf(Remote remote)
        => HostResolver.Parse(remote.Url)?.Host.Id ?? remote.Url;

    /// <summary>
    /// Runs a network operation. Returns null on success, or a <see cref="SyncResult"/>
    /// describing an authentication or connection failure in terms the user can act on.
    /// </summary>
    /// <remarks>
    /// Nothing is rethrown for these cases. libgit2's own message ("could not find
    /// appropriate mechanism for credentials") says nothing useful, and turning an
    /// everyday signed-out state into an exception makes the debugger halt on it during
    /// every development run.
    /// </remarks>
    private static SyncResult? RunNetwork(AuthProbe probe, Action operation)
    {
        try
        {
            operation();
            return null;
        }
        catch (LibGit2SharpException ex) when (probe.WasAsked || IsAuthFailure(ex.Message))
        {
            // The callback fired, so the server wanted credentials. Which message is
            // right depends on whether we had any to give it.
            return probe.HadCredentials
                ? new SyncResult(SyncOutcome.CredentialsRejected,
                    $"{probe.Host} rejected the saved credentials. The token may have expired "
                    + "or lost its scopes — sign in again on the Accounts screen.")
                : new SyncResult(SyncOutcome.NotSignedIn,
                    $"{probe.Host} needs you to be signed in. Open the Accounts screen and add "
                    + $"an account for {probe.Host}, then try again.");
        }
        catch (LibGit2SharpException ex)
        {
            return new SyncResult(SyncOutcome.Failed, $"{probe.Host}: {ex.Message}");
        }
    }

    private static Remote? FindRemote(Repository repo)
        => repo.Network.Remotes["origin"] ?? repo.Network.Remotes.FirstOrDefault();

    /// <summary>Where the current branch stands relative to its copy on the remote.</summary>
    private readonly record struct BranchStanding(
        int Ahead, int Behind, bool HasRemote, bool IsPublished);

    /// <summary>
    /// Counts unpushed and unpulled commits against the branch of the same name on the
    /// remote, and nothing else.
    /// </summary>
    /// <remarks>
    /// The name is the whole question: a branch is published when
    /// <c>refs/remotes/&lt;remote&gt;/&lt;branch&gt;</c> exists, and unpublished when it
    /// does not. That ref is maintained by the fetch refspec whether or not git ever
    /// recorded an upstream, which matters because a repository that was init'ed and
    /// pushed has branches plainly on the remote that track nothing at all.
    ///
    /// <c>TrackingDetails</c> used to be consulted first and is deliberately not any
    /// more. It answers about whatever <c>branch.&lt;name&gt;.merge</c> points at, which
    /// is not always a branch of the same name: <c>git branch -m</c> renames the local
    /// branch and leaves the upstream untouched, so a branch renamed after being pushed
    /// still reports as published and in sync while its new name is on no server
    /// anywhere. That is how "Publish branch" went missing for a branch nobody could
    /// see, and how a pull request was offered from a ref GitHub had never heard of.
    /// If the name isn't there, it's a new branch.
    /// </remarks>
    private static BranchStanding Standing(Repository repo, Branch? head)
    {
        if (FindRemote(repo) is not { } remote)
            return new(0, 0, HasRemote: false, IsPublished: false);

        if (head?.Tip is null)
            return new(0, 0, HasRemote: true, IsPublished: false);

        if (Mirror(repo, remote, head)?.Tip is not { } mirrorTip)
            return new(0, 0, HasRemote: true, IsPublished: false);

        var divergence = repo.ObjectDatabase.CalculateHistoryDivergence(head.Tip, mirrorTip);

        return new(
            divergence.AheadBy ?? 0,
            divergence.BehindBy ?? 0,
            HasRemote: true,
            IsPublished: true);
    }

    /// <summary>The remote-tracking ref for a branch, or null if it was never pushed.</summary>
    private static Branch? Mirror(Repository repo, Remote remote, Branch branch)
        => repo.Branches[$"{remote.Name}/{branch.FriendlyName}"];

    /// <summary>
    /// Points <c>branch.&lt;name&gt;.remote</c> and <c>.merge</c> at the branch of the same
    /// name on the remote, and hands back the branch as it now reads.
    /// </summary>
    /// <remarks>
    /// libgit2 refuses to push or pull a branch that tracks nothing, so without this the
    /// app can see the divergence and still be unable to act on it - and the user has to
    /// drop to the command line for a <c>git push -u</c> to get out, which is exactly the
    /// errand a GUI should save them. This writes what that command would have written.
    ///
    /// An upstream naming a *different* branch is corrected rather than followed. It is
    /// what <c>git branch -m</c> leaves behind, and pushing down it would send this
    /// branch's commits to a branch of another name on the server - silently, since
    /// nothing in the app ever showed that name. Git will not do that either: with the
    /// default <c>push.default=simple</c> it refuses the push outright and says the
    /// upstream does not match the current branch's name.
    /// </remarks>
    private static Branch EnsureTracking(Repository repo, Branch branch, Remote remote)
    {
        if (branch.IsTracking
            && string.Equals(branch.UpstreamBranchCanonicalName, branch.CanonicalName, StringComparison.Ordinal))
        {
            return branch;
        }

        repo.Branches.Update(branch,
            b => b.Remote = remote.Name,
            b => b.UpstreamBranch = branch.CanonicalName);

        return repo.Branches[branch.FriendlyName];
    }

    private static SyncResult NoRemote(string what)
        => new(SyncOutcome.NoRemote, $"This repository has no remote to {what}.");

    private static bool IsAuthFailure(string message)
        => message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
           || message.Contains("401", StringComparison.Ordinal)
           || message.Contains("403", StringComparison.Ordinal)
           || message.Contains("credentials", StringComparison.OrdinalIgnoreCase);

    private static string Short(Commit? commit)
        => commit?.Sha is { Length: >= 7 } sha ? sha[..7] : "HEAD";

    // ---------------------------------------------------------------- helpers

    private static string Discover(string path)
        => Repository.Discover(path)
           ?? throw new InvalidOperationException($"'{path}' is not a git repository.");

    /// <summary>
    /// Builds a synthetic all-added diff for an untracked file, so new files read the
    /// same way in the UI as tracked additions.
    /// </summary>
    private static FileChange DescribeUntracked(string workdir, StatusEntry entry)
    {
        var full = Path.Combine(workdir, entry.FilePath);
        var lines = new List<DiffLine>();
        var additions = 0;

        try
        {
            var info = new FileInfo(full);

            if (info.Exists && info.Length <= MaxUntrackedDiffBytes && !LooksBinary(full))
            {
                var text = File.ReadAllLines(full);
                additions = text.Length;

                lines.Add(new DiffLine
                {
                    Kind = DiffLineKind.HunkHeader,
                    Text = $"@@ -0,0 +1,{text.Length} @@",
                });

                for (var i = 0; i < text.Length && lines.Count < UnifiedDiffParser.MaxLines; i++)
                {
                    lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Added,
                        Text = text[i],
                        NewNumber = (i + 1).ToString(),
                    });
                }
            }
            else if (info.Exists)
            {
                lines.Add(new DiffLine
                {
                    Kind = DiffLineKind.HunkHeader,
                    Text = LooksBinary(full)
                        ? "Binary file - no preview"
                        : $"File too large to preview ({info.Length / 1024} KB)",
                });
            }
        }
        catch (IOException)
        {
            lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = "Unable to read file" });
        }
        catch (UnauthorizedAccessException)
        {
            lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = "Permission denied" });
        }

        return new FileChange
        {
            Path = entry.FilePath,
            Status = ChangeStatus.Added,
            Additions = additions,
            Deletions = 0,
            Diff = lines,
        };
    }

    private static bool LooksBinary(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> buffer = stackalloc byte[BinarySniffBytes];
            var read = stream.Read(buffer);

            return buffer[..read].IndexOf((byte)0) >= 0;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static ChangeStatus ToChangeStatus(ChangeKind kind, FileStatus? state)
    {
        if (state is { } s && s.HasFlag(FileStatus.Conflicted))
            return ChangeStatus.Conflicted;

        return kind switch
        {
            ChangeKind.Added or ChangeKind.Untracked or ChangeKind.Copied => ChangeStatus.Added,
            ChangeKind.Deleted => ChangeStatus.Deleted,
            ChangeKind.Renamed => ChangeStatus.Renamed,
            ChangeKind.Conflicted => ChangeStatus.Conflicted,
            _ => ChangeStatus.Modified,
        };
    }

    /// <summary>
    /// git writes FETCH_HEAD on every fetch, so its mtime is the fetch time. No
    /// FETCH_HEAD means the clone has never been fetched from - returning null keeps
    /// the UI from claiming a fetch that never happened (falling back to the .git
    /// directory's mtime would report "just now" after any commit).
    /// </summary>
    private static DateTimeOffset? LastFetchTime(string gitDir)
    {
        try
        {
            var marker = Path.Combine(gitDir, "FETCH_HEAD");
            if (File.Exists(marker))
                return new DateTimeOffset(File.GetLastWriteTime(marker));
        }
        catch (IOException)
        {
            // Treated the same as never fetched.
        }

        return null;
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}",
        };
    }

    private static readonly string[] AvatarPalette =
    [
        "#3399CC", "#609926", "#C0576B", "#8E6FD8",
        "#2E9E8F", "#B7791F", "#4C7FD1", "#CC6633",
    ];

    /// <summary>
    /// Picks a stable colour for an author. Uses FNV-1a rather than
    /// <see cref="string.GetHashCode()"/>, which is randomised per process and would
    /// give an author a different colour on every launch.
    /// </summary>
    private static string AvatarColour(string key)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in key)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            return AvatarPalette[hash % (uint)AvatarPalette.Length];
        }
    }
}
