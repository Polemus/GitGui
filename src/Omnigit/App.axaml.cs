using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FluentAvalonia.Styling;
using Omnigit.HostProviders;
using Omnigit.Services;
using Omnigit.ViewModels;
using Omnigit.Views;

namespace Omnigit;

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
            // A portable build that updated itself last run left the version before it
            // beside the new one: on Windows the old directory holds the executable the
            // updating process was running from, so it could not be deleted then.
            UpdateService.SweepPreviousVersion();

            var http = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(30) };
            var credentials = CredentialStoreFactory.Create();
            var log = new ActivityLog();

            var viewModel = new MainWindowViewModel(
                new GitService(),
                new RepositoryStore(),
                new FolderPicker(),
                HostProviderRegistry.Create(http),
                new AccountStore(credentials),
                credentials,
                log,
                new SystemShell(),
                new RepositoryWatcher(),
                new UpdateService());

            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Loading repositories touches the disk, so it happens after the window
            // is up rather than blocking first paint.
            desktop.MainWindow.Opened += async (_, _) =>
            {
                await viewModel.InitialiseAsync();
                await RegisterWithDesktopAsync(log);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Gives an AppImage the desktop entry and icons the other package formats
    /// install for themselves, so the dock shows Omnigit's icon rather than the
    /// generic executable one. A no-op everywhere else.
    /// </summary>
    private static async System.Threading.Tasks.Task RegisterWithDesktopAsync(IActivityLog log)
    {
        var result = await DesktopIntegration.EnsureInstalledAsync();

        switch (result.Outcome)
        {
            case DesktopIntegrationOutcome.Installed:
                log.Write(ActivityLevel.Info, "Added Omnigit to the desktop menu", result.Detail);
                break;

            case DesktopIntegrationOutcome.Failed:
                log.Write(
                    ActivityLevel.Warning,
                    "Couldn't add Omnigit to the desktop menu - it will show a generic icon",
                    result.Detail);
                break;
        }
    }
}
