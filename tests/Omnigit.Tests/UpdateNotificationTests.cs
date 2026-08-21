using Omnigit.Services;
using Omnigit.ViewModels;

namespace Omnigit.Tests;

/// <summary>
/// The dot on the settings button, and what the About tab says while an update runs.
/// </summary>
/// <remarks>
/// Both exist because of the same real failure: 0.4.0 was open when 0.4.1 was published,
/// no dot ever appeared, and once the update was started by hand the page said
/// "Downloading…" while it was in fact waiting on a password box that had opened on
/// another screen. Neither is visible to any other test - one is a two-second state
/// nobody happened to be looking at, the other a label.
/// </remarks>
public class UpdateNotificationTests
{
    // ---- The dot -----------------------------------------------------------

    [Fact]
    public async Task A_newer_release_lights_the_dot()
    {
        var model = Model(Available("9.9.9"));

        Assert.False(model.IsUpdateAvailable);

        await model.CheckCommand.ExecuteAsync(null);

        Assert.True(model.IsUpdateAvailable);
        Assert.Equal("9.9.9", model.AvailableVersion);
    }

    [Fact]
    public async Task Being_current_leaves_it_dark()
    {
        var model = Model(new UpdateCheckResult(UpdateCheckOutcome.UpToDate));

        await model.CheckCommand.ExecuteAsync(null);

        Assert.False(model.IsUpdateAvailable);
        Assert.Equal("Omnigit is up to date.", model.StatusLine);
    }

    /// <summary>
    /// A failed download does not mean the release stopped existing. The dot going out
    /// would say it had, and the only way back would be pressing the button again.
    /// </summary>
    [Fact]
    public async Task A_failed_install_leaves_the_dot_lit()
    {
        var service = Available("9.9.9");
        service.ApplyResult = new UpdateApplyResult(UpdateApplyOutcome.Failed, "no room on the disk");

        var model = Model(service);
        await model.CheckCommand.ExecuteAsync(null);
        await model.InstallCommand.ExecuteAsync(null);

        Assert.Equal(UpdateStage.Failed, model.Stage);
        Assert.True(model.IsUpdateAvailable);
        Assert.Equal("no room on the disk", model.Detail);
    }

    /// <summary>
    /// A check that could not happen must not put an error in front of anyone, and must
    /// not claim to have found nothing either.
    /// </summary>
    [Fact]
    public async Task A_check_nobody_asked_for_says_nothing_when_it_fails()
    {
        var model = Model(new UpdateCheckResult(UpdateCheckOutcome.Failed, Detail: "offline"));

        model.OnWindowActivated();
        await Task.Delay(50);

        Assert.Equal(UpdateStage.Idle, model.Stage);
        Assert.Equal(string.Empty, model.StatusLine);
        Assert.False(model.IsUpdateAvailable);
    }

    /// <summary>Returning to the window is what makes a long-open Omnigit notice.</summary>
    [Fact]
    public async Task Returning_to_the_window_checks_again()
    {
        var service = Available("9.9.9");
        var model = Model(service);

        model.OnWindowActivated();
        await Task.Delay(50);

        Assert.Equal(1, service.Checks);
        Assert.True(model.IsUpdateAvailable);

        // But not on every alt-tab: the second is inside the floor and is skipped.
        model.OnWindowActivated();
        await Task.Delay(50);

        Assert.Equal(1, service.Checks);
    }

    // ---- What the page says while it works ---------------------------------

    [Fact]
    public void The_label_stops_saying_downloading_once_it_is_installing()
    {
        var model = Model(Available("9.9.9"));

        model.Stage = UpdateStage.Downloading;
        model.AvailableVersion = "9.9.9";
        Assert.Contains("Downloading", model.StatusLine);

        model.Stage = UpdateStage.Installing;
        Assert.DoesNotContain("Downloading", model.StatusLine);
    }

    /// <summary>
    /// Where a password is needed the label has to say so, and say where to look. The
    /// prompt opened on a second monitor and the update read as a hang.
    /// </summary>
    [Fact]
    public void An_install_that_needs_a_password_says_to_look_for_the_prompt()
    {
        var model = Model(Available("9.9.9", InstallMedium.RpmPackage));

        model.Stage = UpdateStage.Installing;

        Assert.Contains("permission", model.StatusLine);
        Assert.Contains("behind this window", model.StatusLine);
    }

    [Fact]
    public void An_install_that_needs_no_password_just_says_installing()
    {
        var model = Model(Available("9.9.9", InstallMedium.AppImage));

        model.Stage = UpdateStage.Installing;
        model.AvailableVersion = "9.9.9";

        Assert.Equal("Installing Omnigit 9.9.9…", model.StatusLine);
    }

    /// <summary>The progress bar and the button both have to survive the phase change.</summary>
    [Fact]
    public void Installing_still_counts_as_working()
    {
        var model = Model(Available("9.9.9"));

        model.Stage = UpdateStage.Downloading;
        Assert.True(model.IsWorking);

        model.Stage = UpdateStage.Installing;
        Assert.True(model.IsWorking);

        model.Stage = UpdateStage.Available;
        Assert.False(model.IsWorking);
    }

    // ---- Helpers -----------------------------------------------------------

    private static UpdateViewModel Model(IUpdateService service) =>
        new(service, new ActivityLog(), new SilentShell(), designTime: false);

    private static UpdateViewModel Model(UpdateCheckResult result) =>
        Model(new StubUpdateService { CheckResult = result });

    private static StubUpdateService Available(string version, InstallMedium medium = InstallMedium.AppImage)
    {
        var release = new ReleaseInfo(
            Version.Parse(version), "v" + version, "notes", new Uri("https://example.invalid/"), []);

        return new StubUpdateService
        {
            CheckResult = new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, release),
            Location = new InstallLocation(medium, "/somewhere", "/somewhere"),
        };
    }

    private sealed class StubUpdateService : IUpdateService
    {
        public UpdateCheckResult CheckResult { get; set; }
        public UpdateApplyResult ApplyResult { get; set; } = new(UpdateApplyOutcome.Applied);
        public InstallLocation Location { get; set; } = new(InstallMedium.AppImage, "/somewhere");
        public int Checks { get; private set; }

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancel = default)
        {
            Checks++;
            return Task.FromResult(CheckResult);
        }

        public Task<UpdateApplyResult> ApplyAsync(
            ReleaseInfo release, IProgress<UpdateProgress>? progress = null,
            CancellationToken cancel = default)
            => Task.FromResult(ApplyResult);

        public bool Relaunch() => true;
    }

    /// <summary>Nothing here has a window, a browser or a clipboard to reach.</summary>
    private sealed class SilentShell : ISystemShell
    {
        public Task<bool> OpenUrlAsync(Uri url) => Task.FromResult(true);
        public Task<bool> CopyTextAsync(string text) => Task.FromResult(true);
        public Task<bool> ShowInFileManagerAsync(string filePath) => Task.FromResult(true);
        public Task<bool> OpenFileAsync(string filePath) => Task.FromResult(true);
        public bool Shutdown() => true;
    }
}
