namespace Omnigit.Models;

/// <summary>
/// The ahead/behind indicator beside the sync button: what a press would send, and what
/// it would bring down.
/// </summary>
public static class SyncCounts
{
    /// <summary>
    /// Both numbers when the branch has moved on both sides, which is exactly when one of
    /// them alone is misleading: the button can only propose "Pull origin" there, and
    /// showing only what it would pull hides the commits that are still sitting here
    /// unpushed. Empty when the branch is level, so the chip disappears entirely rather
    /// than showing a zero.
    /// </summary>
    public static string Label(int ahead, int behind) => (ahead, behind) switch
    {
        ( > 0, > 0) => $"↑ {ahead}   ↓ {behind}",
        ( > 0, _) => $"↑ {ahead}",
        (_, > 0) => $"↓ {behind}",
        _ => string.Empty,
    };
}
