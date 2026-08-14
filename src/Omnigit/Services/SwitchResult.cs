using System.Collections.Generic;

namespace Omnigit.Services;

/// <summary>How a branch switch ended.</summary>
public enum SwitchOutcome
{
    Succeeded,

    /// <summary>
    /// A file being carried across also differs on the target branch, so checking out
    /// would overwrite uncommitted work. Nothing was changed.
    /// </summary>
    Conflicts,
}

/// <summary>
/// The result of switching branch.
/// </summary>
/// <remarks>
/// Returned rather than thrown, for the same reason as <see cref="SyncResult"/>: carrying
/// a changed file into a branch where that file also differs is an ordinary thing to try,
/// not a fault. libgit2 signals it with a CheckoutConflictException, which travelled up
/// through native frames and halted the debugger on what is really a question for the
/// user - stash it or leave it behind.
/// </remarks>
public sealed record SwitchResult(
    SwitchOutcome Outcome,
    string Message,
    IReadOnlyList<string> ConflictingPaths)
{
    public bool Succeeded => Outcome == SwitchOutcome.Succeeded;

    public static SwitchResult Ok() => new(SwitchOutcome.Succeeded, string.Empty, []);
}
