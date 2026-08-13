using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitGui.HostProviders;
using GitGui.Models;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace GitGui.Services;

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
public sealed class GitService : IGitService
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
        var tracking = head?.TrackingDetails;

        return new RepositoryInfo
        {
            Name = identity?.Name ?? new DirectoryInfo(workdir).Name,
            Owner = identity?.Owner ?? string.Empty,
            Host = identity?.Host ?? HostResolver.LocalOnly,
            LocalPath = workdir,
            DefaultBranch = head?.FriendlyName ?? "HEAD",
            IsPrivate = null, // Not knowable locally; the host API fills this in later.
            Ahead = tracking?.AheadBy ?? 0,
            Behind = tracking?.BehindBy ?? 0,
            HasUpstream = head?.TrackedBranch is not null,
            LastFetched = LastFetchTime(repo.Info.Path),
        };
    }

    public IReadOnlyList<BranchInfo> GetBranches(string path)
    {
        using var repo = new Repository(Discover(path));

        var currentName = repo.Head?.FriendlyName;

        return repo.Branches
            .Where(b => !b.IsRemote)
            .Select(b => new BranchInfo
            {
                Name = b.FriendlyName,
                LastCommitSummary = b.Tip?.MessageShort ?? string.Empty,
                LastCommitAt = b.Tip?.Committer.When ?? DateTimeOffset.MinValue,
                IsCurrent = b.FriendlyName == currentName,
                IsDefault = b.FriendlyName is "main" or "master",
            })
            .OrderByDescending(b => b.IsCurrent)
            .ThenByDescending(b => b.LastCommitAt)
            .ToList();
    }

    public IReadOnlyList<FileChange> GetWorkingChanges(string path)
    {
        using var repo = new Repository(Discover(path));

        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
            DetectRenamesInWorkDir = true,
            DetectRenamesInIndex = true,
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
            })
            .ToList();
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
                        ?? new Signature("GitGui", "gitgui@localhost", DateTimeOffset.Now);

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
                        ?? new Signature("GitGui", "gitgui@localhost", DateTimeOffset.Now);

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
                BuildFetchOptions(credentials, probe, trace), "fetch by GitGui")) is { } failure)
        {
            return failure;
        }

        var behind = repo.Head?.TrackingDetails?.BehindBy ?? 0;

        return SyncResult.Ok(behind > 0
            ? $"Fetched from {remote.Name} — {behind} commit{(behind == 1 ? "" : "s")} to pull"
            : $"Fetched from {remote.Name} — already up to date");
    }

    public SyncResult Pull(string path, GitCredentials? credentials, Action<string>? trace = null)
    {
        using var repo = new Repository(Discover(path));

        var signature = repo.Config.BuildSignature(DateTimeOffset.Now)
                        ?? new Signature("GitGui", "gitgui@localhost", DateTimeOffset.Now);

        var remote = FindRemote(repo);
        if (remote is null)
            return NoRemote("pull from");

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

        // A branch created locally has no upstream, and libgit2 refuses to push it.
        // Setting it here saves the user dropping to the command line for their first
        // push of a new branch, which is exactly when a GUI should help.
        if (!branch.IsTracking)
        {
            repo.Branches.Update(branch,
                b => b.Remote = remote.Name,
                b => b.UpstreamBranch = branch.CanonicalName);

            branch = repo.Branches[branch.FriendlyName];
        }

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

    private static FetchOptions BuildFetchOptions(
        GitCredentials? credentials, AuthProbe probe, Action<string>? trace = null)
    {
        // libgit2 reports transfer progress per object, which would flood the console.
        // Only the crossing of each 10% boundary is reported.
        var lastReported = -1;

        return new FetchOptions
        {
            CredentialsProvider = CredentialsFor(credentials, probe),
            TagFetchMode = TagFetchMode.Auto,
            Prune = true,
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
