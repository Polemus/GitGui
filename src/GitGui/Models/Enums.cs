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
