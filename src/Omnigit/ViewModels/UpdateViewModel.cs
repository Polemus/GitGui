using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Omnigit.Services;

namespace Omnigit.ViewModels;

/// <summary>Where the About section has got to.</summary>
public enum UpdateStage
{
    /// <summary>Nothing has been asked yet.</summary>
    Idle,

    Checking,

    /// <summary>Asked, and this is the newest there is.</summary>
    UpToDate,

    /// <summary>There is a newer release. This is the state the dot is about.</summary>
    Available,

    Downloading,

    /// <summary>Replaced on disk. The app is restarting itself.</summary>
    Applied,

    /// <summary>The check or the download did not work. <see cref="Detail"/> says why.</summary>
    Failed,
}

/// <summary>
/// The About section: what version this is, whether there is a newer one, and the one
/// button that installs it.
/// </summary>
/// <remarks>
/// Its own view model rather than more properties on MainWindowViewModel: none of this
/// is about the open repository, and all of it has a lifetime of its own - a timer, a
/// download in flight, a cancellation. Shown even where the button cannot do anything,
/// since "what version am I running" is the more common question.
/// </remarks>
public sealed partial class UpdateViewModel : ViewModelBase
{
    /// <summary>
    /// How often the app looks. Daily rather than hourly: releases are weeks apart, the
    /// check is a request to somebody else's server, and an update nobody has been told
    /// about for a few hours has cost nothing.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// How long after launch the first check happens. Long enough to be behind opening a
    /// repository, which is what the user is actually waiting for.
    /// </summary>
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);

    /// <summary>How long one check gets before it counts as not having happened.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    private readonly IUpdateService _update;
    private readonly IActivityLog _log;
    private readonly ISystemShell _shell;
    private readonly bool _isDesignTime;

    private DispatcherTimer? _timer;
    private CancellationTokenSource? _download;
    private ReleaseInfo? _release;

    public UpdateViewModel(IUpdateService update, IActivityLog log, ISystemShell shell, bool designTime)
    {
        _update = update;
        _log = log;
        _shell = shell;
        _isDesignTime = designTime;
    }

    // ---- What this copy is -------------------------------------------------

    public string CurrentVersion => AppVersion.Display;

    /// <summary>The commit and the packaging, for a bug report to quote.</summary>
    public string CurrentVersionDetail
    {
        get
        {
            var medium = _update.Location.Medium switch
            {
                InstallMedium.AppImage => "AppImage",
                InstallMedium.Flatpak => "Flatpak",
                InstallMedium.DebPackage => "Debian package",
                InstallMedium.RpmPackage => "RPM package",
                InstallMedium.LinuxTarball => "portable build",
                InstallMedium.WindowsInstaller => "installed build",
                InstallMedium.WindowsPortable => "portable build",
                InstallMedium.MacAppBundle => "app bundle",
                _ => "local build",
            };

            return AppVersion.Commit is { } commit ? $"{medium} · {commit}" : medium;
        }
    }

    /// <summary>Set where something other than this button does the updating.</summary>
    public string? ManagedBy => _update.Location.ManagedBy;

    public bool IsManagedElsewhere => ManagedBy is not null;

    /// <summary>
    /// Warns that a prompt is coming. An administrator dialog nobody was expecting reads
    /// as something having gone wrong, which is the opposite of what it means here.
    /// </summary>
    public string? ElevationNotice => _update.Location switch
    {
        { NeedsElevation: false } => null,
        { Medium: InstallMedium.WindowsInstaller } => "Windows will ask for permission to install it.",
        _ => "You will be asked for your password to install it.",
    };

    public bool NeedsElevation => ElevationNotice is not null;

    // ---- What the check found ----------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking))]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    [NotifyPropertyChangedFor(nameof(IsUpToDate))]
    [NotifyPropertyChangedFor(nameof(HasFailed))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial UpdateStage Stage { get; set; }

    /// <summary>The version on offer, or null when there isn't one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    public partial string? AvailableVersion { get; set; }

    /// <summary>What changed in that release, from the metainfo by way of the release body.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial string? Notes { get; set; }

    /// <summary>Why a check or a download failed, shown under the button.</summary>
    [ObservableProperty]
    public partial string? Detail { get; set; }

    /// <summary>0 to 1 while downloading.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    public bool IsChecking => Stage == UpdateStage.Checking;
    public bool IsDownloading => Stage == UpdateStage.Downloading;
    public bool IsUpToDate => Stage == UpdateStage.UpToDate;
    public bool HasFailed => Stage == UpdateStage.Failed;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>
    /// Drives the dot on the settings button and on the About tab. Deliberately not true
    /// while downloading: the user is looking at the thing, and a badge telling them
    /// about it is noise.
    /// </summary>
    public bool IsUpdateAvailable => Stage == UpdateStage.Available;

    /// <summary>
    /// Whether the button installs anything. An update we found but cannot apply still
    /// shows - with the release page to go to - because knowing is the point.
    /// </summary>
    public bool CanInstall => Stage == UpdateStage.Available && _update.Location.CanSelfUpdate;

    public string StatusLine => Stage switch
    {
        UpdateStage.Checking => "Checking for updates…",
        UpdateStage.UpToDate => "Omnigit is up to date.",
        UpdateStage.Available => $"Omnigit {AvailableVersion} is available.",
        UpdateStage.Downloading => $"Downloading Omnigit {AvailableVersion}…",
        UpdateStage.Applied => $"Updated to {AvailableVersion}. Restarting…",
        UpdateStage.Failed => "Could not check for updates.",
        _ => string.Empty,
    };

    // ---- Checking ----------------------------------------------------------

    /// <summary>
    /// Starts the daily check. Called once the window is up, so the first one is behind
    /// the repository load rather than in front of it.
    /// </summary>
    public void StartChecking()
    {
        if (_isDesignTime || _timer is not null)
            return;

        // One timer, re-armed. The first interval is short so a long-running app is not
        // the only one that ever hears about a release; after that it is daily.
        var timer = new DispatcherTimer { Interval = FirstCheckDelay };
        timer.Tick += (_, _) =>
        {
            timer.Interval = CheckInterval;
            _ = CheckAsync(announce: false);
        };
        timer.Start();

        _timer = timer;
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task Check() => CheckAsync(announce: true);

    private bool CanCheck() => Stage is not (UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Applied);

    /// <summary>
    /// Asks the release feed. <paramref name="announce"/> separates the button from the
    /// timer: a check nobody asked for must not put an error in front of anyone, because
    /// being offline is ordinary and a background action should not punish it - the same
    /// rule the background fetch follows.
    /// </summary>
    private async Task CheckAsync(bool announce)
    {
        if (Stage is UpdateStage.Downloading or UpdateStage.Applied)
            return;

        Stage = UpdateStage.Checking;
        Detail = null;

        // The service's client has a long timeout because the same one fetches an
        // eighty-megabyte AppImage. A check is one small GET, and left to that timeout a
        // server that accepts the connection and says nothing would leave the page
        // reading "Checking for updates…" with the button disabled for half an hour.
        using var timeout = new CancellationTokenSource(CheckTimeout);

        var result = await _update.CheckAsync(timeout.Token).ConfigureAwait(true);

        switch (result.Outcome)
        {
            case UpdateCheckOutcome.UpdateAvailable when result.Release is { } release:
                _release = release;
                AvailableVersion = release.Version.ToString(3);
                Notes = release.Notes;
                Stage = UpdateStage.Available;
                _log.Write(ActivityLevel.Info, $"Omnigit {AvailableVersion} is available.");
                break;

            case UpdateCheckOutcome.UpToDate:
                _release = null;
                AvailableVersion = null;
                Notes = null;
                Stage = UpdateStage.UpToDate;
                if (announce)
                    _log.Write(ActivityLevel.Success, $"Omnigit {CurrentVersion} is up to date.");
                break;

            default:
                Detail = result.Detail;

                // A check nobody asked for goes back to saying nothing at all, rather
                // than leaving "could not check for updates" on a page the user opened
                // to read a version number.
                Stage = announce ? UpdateStage.Failed : UpdateStage.Idle;
                _log.Write(
                    announce ? ActivityLevel.Warning : ActivityLevel.Trace,
                    "Update check failed.",
                    result.Detail);
                break;
        }
    }

    // ---- Installing --------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        if (_release is not { } release)
            return;

        _download?.Cancel();
        _download = new CancellationTokenSource();

        Stage = UpdateStage.Downloading;
        Progress = 0;
        Detail = null;

        _log.Write(ActivityLevel.Info, $"Downloading Omnigit {AvailableVersion}…");

        var progress = new Progress<double>(fraction => Progress = fraction);

        UpdateApplyResult result;
        try
        {
            result = await _update.ApplyAsync(release, progress, _download.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Stage = UpdateStage.Available;
            return;
        }

        if (result.Outcome != UpdateApplyOutcome.Applied)
        {
            Detail = result.Detail;
            Stage = UpdateStage.Failed;
            _log.Write(ActivityLevel.Error, "The update could not be installed.", result.Detail);
            return;
        }

        Stage = UpdateStage.Applied;
        _log.Write(ActivityLevel.Success, $"Updated to Omnigit {AvailableVersion}. Restarting.");

        // Straight into the new one. The button said "Update now", and stopping here to
        // ask a second time would make the one-click promise into a two-click one. What
        // is lost by restarting is an unsent commit message and the current selection;
        // everything else Omnigit knows is in the repository or in a settings file.
        if (!_update.Relaunch())
        {
            Detail = "Omnigit was updated, but could not start the new copy. Launch it yourself.";
            Stage = UpdateStage.Failed;
            return;
        }

        _shell.Shutdown();
    }

    [RelayCommand]
    private async Task OpenReleasePage()
    {
        if (_release is { } release)
            await _shell.OpenUrlAsync(release.Page);
    }
}
