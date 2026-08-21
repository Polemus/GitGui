using System.Collections.Generic;
using System.Linq;
using Omnigit.Models;

namespace Omnigit.ViewModels;

/// <summary>
/// The question asked before a clone is moved to the trash: which one, from where, and
/// what is about to go with it that exists nowhere else.
/// </summary>
/// <remarks>
/// Removing a repository from the list needs no dialog - the files stay and re-adding
/// takes one folder picker. Deleting one does, and this is what it says.
///
/// The warnings are the reason this is a view model rather than a message string. A
/// clone that is level with its remote is a copy of something the server still has; one
/// with uncommitted edits, unpushed commits or a stash is not, and the difference is
/// invisible from the sidebar. Every git client that has ever lost someone's work lost
/// it because the destructive action looked the same in both cases.
/// </remarks>
public sealed class RepositoryRemovalViewModel
{
    public required RepositoryInfo Repository { get; init; }

    /// <summary>Where it is, shown in full: deleting the wrong one is the failure here.</summary>
    public required string Path { get; init; }

    /// <summary>Files changed but not committed.</summary>
    public required int UncommittedChanges { get; init; }

    /// <summary>Commits on the checked-out branch the remote has not got.</summary>
    public required int UnpushedCommits { get; init; }

    /// <summary>1 when the checked-out branch is on no server at all, 0 otherwise.</summary>
    public required int UnpublishedBranches { get; init; }

    public required int Stashes { get; init; }

    public string Title => $"Delete {Repository.Name}?";

    /// <summary>
    /// Named for what actually happens. "Delete" would be a lie about the Recycle Bin and
    /// an alarm about nothing; "Move to trash" says both that it goes and that it can
    /// come back.
    /// </summary>
    public string ConfirmLabel => "Move to trash";

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>
    /// Everything in this clone that the remote does not have. Deliberately counted
    /// rather than summarised - "you have unsaved work" is ignorable, "4 uncommitted
    /// changes and 2 unpushed commits" is not.
    /// </summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var warnings = new List<string>();

            if (UncommittedChanges > 0)
                warnings.Add(Count(UncommittedChanges, "uncommitted change", "uncommitted changes"));

            if (UnpushedCommits > 0)
                warnings.Add(Count(UnpushedCommits, "unpushed commit", "unpushed commits"));

            if (UnpublishedBranches > 0)
                warnings.Add("a branch that is on no server");

            if (Stashes > 0)
                warnings.Add(Count(Stashes, "stash", "stashes"));

            return warnings;
        }
    }

    public string WarningSummary => Warnings.Count == 0
        ? string.Empty
        : "This clone holds " + Join(Warnings) + " — none of which are on the remote.";

    public string Summary => HasWarnings
        ? "The whole folder goes to the trash, where you can put it back from."
        : "The whole folder goes to the trash. Everything in it has been pushed, so the "
          + "remote still has it either way.";

    private static string Count(int n, string one, string many) =>
        $"{n} {(n == 1 ? one : many)}";

    /// <summary>"a, b and c" - an Oxford-comma-free list, because it is read aloud in the head.</summary>
    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };
}
