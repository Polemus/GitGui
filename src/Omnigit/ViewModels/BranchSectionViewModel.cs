using System;
using System.Collections.Generic;
using System.Linq;
using Omnigit.Models;

namespace Omnigit.ViewModels;

/// <summary>
/// One labelled run of branches in the picker - "Default branch", "Recent branches" and
/// so on. Rebuilt whole whenever the list or the filter changes, so nothing here is
/// observable: the collection of sections is what the view watches.
/// </summary>
/// <remarks>
/// Sections rather than one flat list because a repository of any age has more branches
/// than fit in a dropdown, and the two anyone wants - the default, and the one they were
/// just on - are otherwise somewhere in the middle of the rest. This is the shape GitHub
/// Desktop's picker uses, for the same reason.
/// </remarks>
public sealed class BranchSectionViewModel
{
    /// <summary>How many branches sit under "Recent" before the rest are folded away.</summary>
    public const int RecentCount = 5;

    public required string Header { get; init; }

    public required IReadOnlyList<BranchInfo> Branches { get; init; }

    /// <summary>Empty sections are dropped by <see cref="Build"/> rather than hidden.</summary>
    public bool HasBranches => Branches.Count > 0;

    /// <summary>
    /// Groups the branches the way the picker draws them, after filtering. Filtering
    /// first is the point: a name buried in "Other branches" is then one keystroke away,
    /// rather than needing the group it happens to have landed in.
    /// </summary>
    /// <param name="branches">
    /// The list as <c>GetBranches</c> gives it - current first, then newest commit first.
    /// That order is what "recent" means here; git records no per-branch checkout history
    /// for us to read, so the last commit on a branch is the only clue a clone carries.
    /// </param>
    /// <param name="filter">Matched as a case-insensitive substring of the branch name.</param>
    public static IReadOnlyList<BranchSectionViewModel> Build(
        IEnumerable<BranchInfo> branches, string filter)
    {
        filter = filter.Trim();

        var matching = branches
            .Where(b => filter.Length == 0
                        || b.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var local = matching.Where(b => !b.IsRemoteOnly).ToList();
        var sections = new List<BranchSectionViewModel>();

        void Add(string header, IEnumerable<BranchInfo> section)
        {
            var list = section.ToList();

            if (list.Count > 0)
                sections.Add(new BranchSectionViewModel { Header = header, Branches = list });
        }

        if (filter.Length == 0)
        {
            Add("Default branch", local.Where(b => b.IsDefault));

            var rest = local.Where(b => !b.IsDefault).ToList();

            Add("Recent branches", rest.Take(RecentCount));
            Add("Other branches", rest.Skip(RecentCount)
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            // One list while filtering: the groups exist to shorten a long list, and the
            // filter has already done that.
            Add("Branches", local);
        }

        // Last, and under their own heading: checking one of these out creates a branch
        // here, which is a different thing from switching to one that already exists.
        Add("On the remote only", matching
            .Where(b => b.IsRemoteOnly)
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase));

        return sections;
    }
}
