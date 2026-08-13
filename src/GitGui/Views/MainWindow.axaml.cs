using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using GitGui.ViewModels;

namespace GitGui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateThemeIcon();
        DataContextChanged += OnDataContextChanged;
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
    private void OnRepositoryRowClick(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() => RepositoryPickerButton.Flyout?.Hide());

    private void OnBranchRowClick(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() => BranchPickerButton.Flyout?.Hide());

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
