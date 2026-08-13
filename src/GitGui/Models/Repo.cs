using System;
using System.Collections.Generic;

namespace GitGui.Models;

/// <summary>A local clone, plus the remote it tracks.</summary>
public sealed class RepositoryInfo
{
    public required string Name { get; init; }
    public required string Owner { get; init; }
    public required GitHost Host { get; init; }
    public required string LocalPath { get; init; }
    public required string DefaultBranch { get; init; }

    /// <summary>
    /// Null when unknown. Visibility is a property of the forge, not the clone, so
    /// it stays null until a host API tells us.
    /// </summary>
    public bool? IsPrivate { get; init; }

    /// <summary>Commits on the local branch not yet pushed.</summary>
    public int Ahead { get; init; }

    /// <summary>Commits on the remote branch not yet merged locally.</summary>
    public int Behind { get; init; }

    /// <summary>
    /// False when the current branch has never been pushed. Lets the UI tell "nothing to
    /// push" apart from "nowhere to push to", which decides whether amending is safe.
    /// </summary>
    public bool HasUpstream { get; init; }

    /// <summary>Null when the clone has never been fetched from.</summary>
    public DateTimeOffset? LastFetched { get; init; }

    /// <summary>
    /// True when HEAD points straight at a commit rather than a branch, which is what
    /// checking out an older commit leaves behind. Commits made here belong to no branch.
    /// </summary>
    public bool IsDetached { get; init; }

    /// <summary>The commit HEAD is on. Empty in a repository with no commits yet.</summary>
    public string HeadSha { get; init; } = string.Empty;

    /// <summary>A merge, revert or cherry-pick git is part-way through.</summary>
    public RepositoryOperation Operation { get; init; }

    /// <summary>Paths git could not merge on its own, waiting on the user.</summary>
    public int ConflictCount { get; init; }

    public string HeadShortSha => HeadSha.Length > 7 ? HeadSha[..7] : HeadSha;

    public string FullName => string.IsNullOrEmpty(Owner) ? Name : $"{Owner}/{Name}";

    /// <summary>
    /// Where this clone came from, for the line above the name in the picker. Owner and
    /// host rather than <see cref="FullName"/> and host, which would repeat the name
    /// printed directly underneath it.
    /// </summary>
    public string OriginLabel => string.IsNullOrEmpty(Owner)
        ? Host.Name
        : $"{Owner} · {Host.Name}";
    public bool HasVisibility => IsPrivate.HasValue;
    public string VisibilityLabel => IsPrivate == true ? "Private" : "Public";

    public string LastFetchedLabel => LastFetched is { } when
        ? $"Last fetched {TimeFormat.Relative(when)}"
        : "Never fetched";
    public string HostLabel => $"{Host.KindLabel} · {Host.Name}";
}

public sealed class BranchInfo
{
    public required string Name { get; init; }
    public required string LastCommitSummary { get; init; }
    public DateTimeOffset LastCommitAt { get; init; }
    public bool IsDefault { get; init; }

    /// <summary>True for the branch HEAD currently points at.</summary>
    public bool IsCurrent { get; init; }

    public string RelativeTime => TimeFormat.Relative(LastCommitAt);
}

/// <summary>
/// One entry on the stash stack. <see cref="BranchName"/> is read back out of git's own
/// message ("On main: …"), which is the only place the originating branch is recorded.
/// </summary>
public sealed class StashInfo
{
    /// <summary>Position on the stack. Shifts as entries are added or dropped.</summary>
    public required int Index { get; init; }

    public required string Message { get; init; }

    public required string BranchName { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string RelativeTime => TimeFormat.Relative(CreatedAt);
}

public sealed class CommitInfo
{
    public required string Sha { get; init; }
    public required string Summary { get; init; }
    public required string AuthorName { get; init; }
    public required string AuthorInitials { get; init; }
    public required string AvatarHex { get; init; }
    public DateTimeOffset CommittedAt { get; init; }
    /// <summary>
    /// Zero until the commit is selected. Counting changed files means diffing every
    /// commit, which is far too slow to do for a whole history list, so the view model
    /// loads the file list on demand and reports the count from there.
    /// </summary>
    public int FilesChanged { get; init; }

    /// <summary>Tag names pointing at this commit, e.g. "v1.2.0". Usually empty.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public bool HasTags => Tags.Count > 0;

    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
    public string RelativeTime => TimeFormat.Relative(CommittedAt);

    public string FilesChangedLabel =>
        FilesChanged == 1 ? "1 file changed" : $"{FilesChanged} files changed";
}

/// <summary>One changed path in the working tree, with its rendered diff.</summary>
public sealed class FileChange
{
    public required string Path { get; init; }
    public required ChangeStatus Status { get; init; }
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public IReadOnlyList<DiffLine> Diff { get; init; } = [];

    public string FileName
    {
        get
        {
            var i = Path.LastIndexOf('/');
            return i < 0 ? Path : Path[(i + 1)..];
        }
    }

    public string Directory
    {
        get
        {
            var i = Path.LastIndexOf('/');
            return i < 0 ? string.Empty : Path[..i];
        }
    }

    /// <summary>Single-letter status marker, matching git's short format.</summary>
    public string StatusGlyph => Status switch
    {
        ChangeStatus.Added => "A",
        ChangeStatus.Modified => "M",
        ChangeStatus.Deleted => "D",
        ChangeStatus.Renamed => "R",
        ChangeStatus.Conflicted => "!",
        _ => "?",
    };

    // Styling hooks — bound to Classes.* in the views so colours live in the theme.
    public bool IsAdded => Status == ChangeStatus.Added;
    public bool IsModified => Status == ChangeStatus.Modified;
    public bool IsDeleted => Status == ChangeStatus.Deleted;
    public bool IsRenamed => Status == ChangeStatus.Renamed;
    public bool IsConflicted => Status == ChangeStatus.Conflicted;
}

public sealed class DiffLine
{
    public required DiffLineKind Kind { get; init; }
    public required string Text { get; init; }

    /// <summary>Line number in the pre-image, or empty for added lines.</summary>
    public string OldNumber { get; init; } = string.Empty;

    /// <summary>Line number in the post-image, or empty for removed lines.</summary>
    public string NewNumber { get; init; } = string.Empty;

    public string Marker => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => " ",
    };

    // Styling hooks — bound to Classes.* in DiffView.
    public bool IsAdded => Kind == DiffLineKind.Added;
    public bool IsRemoved => Kind == DiffLineKind.Removed;
    public bool IsHunkHeader => Kind == DiffLineKind.HunkHeader;
}
