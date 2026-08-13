using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FluentAvalonia.Styling;
using GitGui.HostProviders;
using GitGui.Services;
using GitGui.ViewModels;
using GitGui.Views;

namespace GitGui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ApplyBrandAccent();
    }

    /// <summary>
    /// Pushes the BrandAccentColor design token into FluentAvalonia, which derives
    /// the SystemAccentColor* palette that stock controls (CheckBox, ToggleSwitch,
    /// Slider, focus rings) render with.
    /// </summary>
    /// <remarks>
    /// Our own styles read AccentBrush from Tokens.axaml; stock controls can't. Doing
    /// this in code keeps both fed from the single token rather than a duplicated
    /// literal in App.axaml that would drift when the brand colour changes.
    /// </remarks>
    private void ApplyBrandAccent()
    {
        if (!Resources.TryGetResource("BrandAccentColor", null, out var value)
            || value is not Color accent)
        {
            return;
        }

        foreach (var theme in Styles.OfType<FluentAvaloniaTheme>())
            theme.CustomAccentColor = accent;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var http = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(30) };
            var credentials = CredentialStoreFactory.Create();

            var viewModel = new MainWindowViewModel(
                new GitService(),
                new RepositoryStore(),
                new FolderPicker(),
                HostProviderRegistry.Create(http),
                new AccountStore(credentials),
                credentials,
                new ActivityLog(),
                new SystemShell(),
                new RepositoryWatcher());

            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Loading repositories touches the disk, so it happens after the window
            // is up rather than blocking first paint.
            desktop.MainWindow.Opened += async (_, _) => await viewModel.InitialiseAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
