using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace GitGui.Views;

public partial class RepositoryView : UserControl
{
    public RepositoryView() => InitializeComponent();

    // Both lists want the same two things from a right-click, so they share the handlers
    // below and differ only in which flyout hangs off the ListBox.

    private void OnHistoryContextRequested(object? sender, ContextRequestedEventArgs e)
        => SelectRowUnder(sender, e.Source);

    private void OnHistoryPointerReleased(object? sender, PointerReleasedEventArgs e)
        => OpenRowFlyout(sender, e);

    private void OnChangesContextRequested(object? sender, ContextRequestedEventArgs e)
        => SelectRowUnder(sender, e.Source);

    private void OnChangesPointerReleased(object? sender, PointerReleasedEventArgs e)
        => OpenRowFlyout(sender, e);

    /// <summary>
    /// The fallback that actually gets the menu up. ContextRequested is raised by the
    /// platform backend, and when it doesn't arrive the flyout never opens with no way to
    /// tell from inside the app - so the right-button release opens it too, skipping if
    /// the event did arrive and the flyout is already up.
    /// </summary>
    private static void OpenRowFlyout(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || sender is not ListBox list)
            return;

        SelectRowUnder(list, e.Source);

        // Placement="Pointer" on the flyout itself is what puts it under the row that was
        // clicked; ShowAt in Avalonia 12.1 takes the target only.
        if (list.ContextFlyout is { IsOpen: false } flyout)
            flyout.ShowAt(list);
    }

    /// <summary>
    /// Right-clicking a row has to select it first: Avalonia leaves the selection alone on
    /// a right-click, so the menu would otherwise act on whichever row was last
    /// left-clicked. Selecting is also what lets the menu's commands read the selection
    /// rather than a CommandParameter that goes null as the popup opens.
    /// </summary>
    private static void SelectRowUnder(object? sender, object? source)
    {
        if (sender is not ListBox list || source is not Visual visual)
            return;

        if (visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: { } item })
            list.SelectedItem = item;
    }
}
