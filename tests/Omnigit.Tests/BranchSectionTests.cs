using Omnigit.Models;
using Omnigit.ViewModels;

namespace Omnigit.Tests;

/// <summary>
/// How the branch picker groups and filters what it was given. Pure over a list, so it
/// is tested here rather than by driving the dropdown.
/// </summary>
public class BranchSectionTests
{
    private static BranchInfo Branch(
        string name, bool isDefault = false, bool isCurrent = false, bool remoteOnly = false,
        int daysOld = 0)
        => new()
        {
            Name = name,
            LastCommitSummary = $"work on {name}",
            LastCommitAt = DateTimeOffset.Now.AddDays(-daysOld),
            IsDefault = isDefault,
            IsCurrent = isCurrent,
            IsRemoteOnly = remoteOnly,
            RemoteName = remoteOnly ? "origin" : string.Empty,
        };

    private static IReadOnlyList<string> Headers(IEnumerable<BranchSectionViewModel> sections)
        => sections.Select(s => s.Header).ToList();

    private static IReadOnlyList<string> Names(BranchSectionViewModel section)
        => section.Branches.Select(b => b.Name).ToList();

    [Fact]
    public void The_default_branch_gets_its_own_section_above_the_rest()
    {
        var sections = BranchSectionViewModel.Build(
            [Branch("feature", isCurrent: true), Branch("main", isDefault: true)], string.Empty);

        Assert.Equal(["Default branch", "Recent branches"], Headers(sections));
        Assert.Equal(["main"], Names(sections[0]));
        Assert.Equal(["feature"], Names(sections[1]));
    }

    [Fact]
    public void Only_the_first_few_are_recent_and_the_remainder_is_sorted_by_name()
    {
        var branches = Enumerable.Range(1, BranchSectionViewModel.RecentCount + 3)
            .Select(i => Branch($"branch-{i:00}", daysOld: i))
            .ToList();

        var sections = BranchSectionViewModel.Build(branches, string.Empty);

        Assert.Equal(["Recent branches", "Other branches"], Headers(sections));
        Assert.Equal(BranchSectionViewModel.RecentCount, sections[0].Branches.Count);

        // Recent keeps the order it arrived in - newest commit first.
        Assert.Equal("branch-01", sections[0].Branches[0].Name);

        // The tail is alphabetical: past the recent handful, a name is how anything is
        // found, and its commit date says nothing useful.
        Assert.Equal(["branch-06", "branch-07", "branch-08"], Names(sections[1]));
    }

    [Fact]
    public void Branches_only_on_the_remote_are_kept_apart_from_the_ones_that_are_here()
    {
        var sections = BranchSectionViewModel.Build(
            [Branch("main", isDefault: true), Branch("theirs", remoteOnly: true)], string.Empty);

        Assert.Equal(["Default branch", "On the remote only"], Headers(sections));
        Assert.Equal(["theirs"], Names(sections[1]));
    }

    [Fact]
    public void Filtering_collapses_the_local_groups_into_one_and_matches_anywhere_in_a_name()
    {
        var sections = BranchSectionViewModel.Build(
            [
                Branch("main", isDefault: true),
                Branch("fix/Login-Crash"),
                Branch("feature/login", remoteOnly: true),
                Branch("docs"),
            ],
            "LOGIN");

        Assert.Equal(["Branches", "On the remote only"], Headers(sections));
        Assert.Equal(["fix/Login-Crash"], Names(sections[0]));
        Assert.Equal(["feature/login"], Names(sections[1]));
    }

    /// <summary>
    /// The picker shows an explanation instead, and Enter has nothing to check out - both
    /// of which read the section list being empty rather than testing the filter again.
    /// </summary>
    [Fact]
    public void A_filter_matching_nothing_produces_no_sections_at_all()
    {
        var sections = BranchSectionViewModel.Build(
            [Branch("main", isDefault: true)], "nothing-like-this");

        Assert.Empty(sections);
    }
}
