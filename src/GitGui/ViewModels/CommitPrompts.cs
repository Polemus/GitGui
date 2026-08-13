using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitGui.Models;

namespace GitGui.ViewModels;

/// <summary>
/// What every question asked about a commit has in common: which commit, and enough of
/// it shown back to the user that they can see they picked the right one.
/// </summary>
public abstract partial class CommitPromptViewModel(CommitInfo commit) : ObservableObject
{
    public string Sha { get; } = commit.Sha;

    public string ShortSha { get; } = commit.ShortSha;

    public string Summary { get; } = commit.Summary;

    /// <summary>The line under the title, naming the commit being acted on.</summary>
    public string CommitLabel => $"{ShortSha} — {Summary}";
}

/// <summary>Naming a branch that starts at some commit other than the newest.</summary>
public sealed partial class BranchFromCommitViewModel(CommitInfo commit)
    : CommitPromptViewModel(commit)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    public partial string Name { get; set; } = string.Empty;

    public bool CanCreate => !string.IsNullOrWhiteSpace(Name);
}

/// <summary>Naming a tag, and optionally saying what it marks.</summary>
public sealed partial class TagDraftViewModel(CommitInfo commit) : CommitPromptViewModel(commit)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>
    /// Empty writes a plain name pointing at the commit; anything else makes git record
    /// a tag object with an author and a date, which is what a release wants.
    /// </summary>
    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    public bool CanCreate => !string.IsNullOrWhiteSpace(Name);
}

/// <summary>
/// Choosing which branch a commit's changes get copied onto. Only branches other than
/// the one already checked out are offered - copying a commit onto the branch it is
/// already on would just duplicate it.
/// </summary>
public sealed partial class CherryPickDraftViewModel : CommitPromptViewModel
{
    public CherryPickDraftViewModel(CommitInfo commit, IEnumerable<string> branches)
        : base(commit)
    {
        foreach (var branch in branches)
            Branches.Add(branch);

        TargetBranch = Branches.Count > 0 ? Branches[0] : null;
    }

    public ObservableCollection<string> Branches { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial string? TargetBranch { get; set; }

    public bool HasBranches => Branches.Count > 0;

    public bool CanApply => !string.IsNullOrWhiteSpace(TargetBranch);

    public string EmptyLabel => "There is no other branch to copy this onto. Make one first.";
}

/// <summary>
/// Moving the branch back to a commit, and how much of the work since then survives.
/// </summary>
/// <remarks>
/// The three modes are git's own, and the wording is the whole point of the dialog:
/// "mixed" and "hard" mean nothing to someone who has not read the man page, and one of
/// them destroys uncommitted work.
/// </remarks>
public sealed partial class ResetDraftViewModel(CommitInfo commit) : CommitPromptViewModel(commit)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSoft))]
    [NotifyPropertyChangedFor(nameof(IsMixed))]
    [NotifyPropertyChangedFor(nameof(IsHard))]
    [NotifyPropertyChangedFor(nameof(Explanation))]
    [NotifyPropertyChangedFor(nameof(ConfirmLabel))]
    public partial ResetKind Kind { get; set; } = ResetKind.Mixed;

    public bool IsSoft => Kind == ResetKind.Soft;
    public bool IsMixed => Kind == ResetKind.Mixed;
    public bool IsHard => Kind == ResetKind.Hard;

    // Set through commands rather than two-way bindings, for the same reason the branch
    // switch prompt does: picking one must never leave both looking unselected.
    [RelayCommand] private void ChooseSoft() => Kind = ResetKind.Soft;
    [RelayCommand] private void ChooseMixed() => Kind = ResetKind.Mixed;
    [RelayCommand] private void ChooseHard() => Kind = ResetKind.Hard;

    public string Explanation => Kind switch
    {
        ResetKind.Soft =>
            $"Everything committed after {ShortSha} comes back as changes ready to commit again. "
            + "Nothing is lost.",

        ResetKind.Hard =>
            $"Everything committed after {ShortSha}, and everything uncommitted, is thrown away. "
            + "This cannot be undone.",

        _ =>
            $"Everything committed after {ShortSha} comes back as changes in your working tree, "
            + "unstaged. Nothing is lost.",
    };

    public string ConfirmLabel => IsHard ? "Reset and discard" : "Reset";
}
