using System.Collections.Generic;

namespace GitGui.Services;

/// <summary>How reverting or cherry-picking a commit ended.</summary>
public enum CommitOperationOutcome
{
    Succeeded,

    /// <summary>
    /// git applied what it could and stopped. The repository is mid-operation, the
    /// conflicted files carry markers, and the user has to finish or abandon it.
    /// </summary>
    Conflicts,

    /// <summary>The commit's changes are already in the tree, so there was nothing to do.</summary>
    NothingToDo,

    /// <summary>
    /// Refused before anything was touched — a dirty working tree, or another operation
    /// still in progress. Nothing changed, so the message says what to clear up first.
    /// </summary>
    Refused,
}

/// <summary>
/// The result of applying one commit's changes somewhere else.
/// </summary>
/// <remarks>
/// Returned rather than thrown, for the same reason as <see cref="SwitchResult"/>:
/// conflicting with what is already there is an ordinary outcome of cherry-picking, not
/// a fault, and modelling it as an exception makes the debugger halt on every one.
/// </remarks>
public sealed record CommitOperationResult(
    CommitOperationOutcome Outcome,
    string Message,
    IReadOnlyList<string> ConflictingPaths)
{
    public bool Succeeded => Outcome == CommitOperationOutcome.Succeeded;

    public bool HasConflicts => Outcome == CommitOperationOutcome.Conflicts;

    public static CommitOperationResult Ok(string message)
        => new(CommitOperationOutcome.Succeeded, message, []);

    public static CommitOperationResult Refused(string message)
        => new(CommitOperationOutcome.Refused, message, []);
}
