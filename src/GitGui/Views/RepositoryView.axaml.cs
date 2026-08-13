using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace GitGui.Views;

public partial class RepositoryView : UserControl
{
    public RepositoryView() => InitializeComponent();

    /// <summary>
    /// Right-clicking a commit has to select it first: Avalonia leaves the selection
    /// alone on a right-click, so the menu would otherwise act on whichever commit was
    /// last left-clicked. Selecting is also what lets the menu's commands read
    /// SelectedCommit rather than a CommandParameter that goes null as the popup opens.
    /// </summary>
    private void OnHistoryContextRequested(object? sender, ContextRequestedEventArgs e)
        => SelectRowUnder(sender, e.Source);

    /// <summary>
    /// The fallback that actually gets the menu up. ContextRequested is raised by the
    /// platform backend, and when it doesn't arrive the flyout never opens and there is
    /// no way to tell from inside the app - so the right-button release opens it too,
    /// skipping if the event did arrive and the flyout is already up.
    /// </summary>
    private void OnHistoryPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || sender is not ListBox list)
            return;

        SelectRowUnder(list, e.Source);

        // Placement="Pointer" on the flyout itself is what puts it under the commit that
        // was clicked; ShowAt in Avalonia 12.1 takes the target only.
        if (list.ContextFlyout is { IsOpen: false } flyout)
            flyout.ShowAt(list);
    }

    /// <summary>Walks out from whatever was hit to the row that contains it.</summary>
    private static void SelectRowUnder(object? sender, object? source)
    {
        if (sender is not ListBox list || source is not Visual visual)
            return;

        if (visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: { } item })
            list.SelectedItem = item;
    }
}
