namespace GitGui.Models;

/// <summary>The kind of git forge a remote lives on. Drives auth flow and API dialect.</summary>
public enum HostKind
{
    GitHub,
    Gitea,
}

/// <summary>Working-tree status of a single path.</summary>
public enum ChangeStatus
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Conflicted,
}

/// <summary>Role of one rendered line inside a unified diff.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    HunkHeader,
}

/// <summary>
/// A multi-step git operation the repository is part-way through. Everything except
/// <see cref="None"/> means git is waiting for the user to finish or abandon it, which
/// is nearly always because something conflicted.
/// </summary>
public enum RepositoryOperation
{
    None,
    Merge,
    Revert,
    CherryPick,
    Rebase,

    /// <summary>Something we don't model, e.g. a bisect. Reported, not acted on.</summary>
    Other,
}

/// <summary>
/// How much of the working tree a reset takes with it. Named Kind rather than Mode so
/// it doesn't collide with libgit2's own <c>ResetMode</c> where both are in scope.
/// </summary>
public enum ResetKind
{
    /// <summary>Moves the branch only; every change stays staged.</summary>
    Soft,

    /// <summary>Moves the branch and unstages; the files themselves are untouched.</summary>
    Mixed,

    /// <summary>Moves the branch and throws the changes away. Unrecoverable.</summary>
    Hard,
}

/// <summary>Which version of a conflicted file to keep.</summary>
public enum ConflictSide
{
    /// <summary>What was already here — git's "ours".</summary>
    Mine,

    /// <summary>What the commit being applied brings — git's "theirs".</summary>
    Theirs,
}
