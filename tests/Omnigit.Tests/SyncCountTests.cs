using Omnigit.Models;

namespace Omnigit.Tests;

/// <summary>
/// The number beside the sync button. One direction at a time was the bug: a branch both
/// ahead and behind showed only what a pull would bring down, and the commits sitting
/// here unpushed went unmentioned by the one control that talks about the remote.
/// </summary>
public class SyncCountTests
{
    [Fact]
    public void A_level_branch_has_no_indicator_at_all()
        => Assert.Equal(string.Empty, SyncCounts.Label(ahead: 0, behind: 0));

    [Fact]
    public void Commits_to_send_are_counted_upwards()
        => Assert.Equal("↑ 2", SyncCounts.Label(ahead: 2, behind: 0));

    [Fact]
    public void Commits_to_bring_down_are_counted_downwards()
        => Assert.Equal("↓ 3", SyncCounts.Label(ahead: 0, behind: 3));

    [Fact]
    public void A_branch_that_has_moved_on_both_sides_shows_both_numbers()
    {
        var label = SyncCounts.Label(ahead: 2, behind: 3);

        Assert.Contains("↑ 2", label);
        Assert.Contains("↓ 3", label);
    }
}
