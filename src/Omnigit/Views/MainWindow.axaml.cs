using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Omnigit.ViewModels;

namespace Omnigit.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateThemeIcon();
        DataContextChanged += OnDataContextChanged;

        // Coming back to the window is when someone might act on a new release, so it is
        // the moment worth spending a request on. The view model decides whether enough
        // time has passed to bother; a timer alone cannot help a window left open for
        // days, because it fires on its own schedule rather than on anyone's attention.
        Activated += (_, _) =>
        {
            if (DataContext is MainWindowViewModel model)
                model.Update.OnWindowActivated();
        };

        // The branch picker opens onto its filter box, so it can be driven from the
        // keyboard alone. The flyout builds its content on open, which is why this hangs
        // off the flyout's event rather than the box's own Loaded.
        if (BranchPickerButton.Flyout is { } picker)
        {
            picker.Opened += (_, _) =>
                Dispatcher.UIThread.Post(() => BranchFilterBox.Focus(), DispatcherPriority.Input);

            // A filter left behind would greet the next open with a list of one.
            picker.Closed += (_, _) =>
            {
                if (DataContext is MainWindowViewModel model)
                    model.BranchFilter = string.Empty;
            };
        }
    }

    /// <summary>
    /// Enter takes the top match, Escape clears the box before the flyout gets the key
    /// and closes on the first press - a filter is worth undoing without losing the list.
    /// </summary>
    private void OnBranchFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;

            // Order matters: hiding the flyout clears the filter below, which rebuilds
            // the list unfiltered - and the top match would then be the top of every
            // branch in the repository rather than of the ones that were on screen.
            model.CheckoutTopMatchCommand.Execute(null);
            BranchPickerButton.Flyout?.Hide();
            return;
        }

        if (e.Key == Key.Escape && model.BranchFilter.Length > 0)
        {
            e.Handled = true;
            model.BranchFilter = string.Empty;
        }
    }

    /// <summary>
    /// Keeps the activity console pinned to the newest line, the way a terminal does.
    /// Without this the interesting part scrolls out of view during a fetch.
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel model)
            return;

        ((INotifyCollectionChanged)model.LogEntries).CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add)
                Dispatcher.UIThread.Post(() => LogScroller.ScrollToEnd(), DispatcherPriority.Background);
        };
    }

    // Flyouts don't dismiss themselves when a templated row is clicked, so these
    // close them explicitly.
    //
    // The Post is load-bearing. Button raises Click *before* invoking Command, and
    // hiding a flyout tears down the popup's visual tree - which detaches the row's
    // DataContext and makes CommandParameter re-evaluate to null. Deferring the hide
    // to the next dispatcher pass lets the command run first, with its parameter
    // still intact.
    private void OnBranchRowClick(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() => BranchPickerButton.Flyout?.Hide());

    private void OnAddRepositoryRowClick(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() => AddRepositoryButton.Flyout?.Hide());

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is not { } app)
            return;

        app.RequestedThemeVariant = app.ActualThemeVariant == ThemeVariant.Dark
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

        UpdateThemeIcon();
    }

    private void UpdateThemeIcon()
    {
        var key = Application.Current?.ActualThemeVariant == ThemeVariant.Dark
            ? "IconMoon"
            : "IconSun";

        if (this.TryFindResource(key, out var geometry) && geometry is Avalonia.Media.Geometry g)
            ThemeIcon.Data = g;
    }
}
