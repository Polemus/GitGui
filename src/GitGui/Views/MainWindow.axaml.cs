using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace GitGui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateThemeIcon();
    }

    // Flyouts don't dismiss themselves when a templated row is clicked, so the
    // row handlers close them explicitly after the bound command has run.
    private void OnRepositoryRowClick(object? sender, RoutedEventArgs e)
        => RepositoryPickerButton.Flyout?.Hide();

    private void OnBranchRowClick(object? sender, RoutedEventArgs e)
        => BranchPickerButton.Flyout?.Hide();

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
