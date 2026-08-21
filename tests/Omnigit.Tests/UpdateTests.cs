using System.Net;
using System.Text.RegularExpressions;
using Omnigit.Services;

namespace Omnigit.Tests;

/// <summary>
/// The update check: reading a version, working out which package this copy came from,
/// and naming the asset that would replace it.
/// </summary>
/// <remarks>
/// The asset names are held against build/expected-artifacts.sh rather than against a
/// list written here. A filename invented in C# that no release contains would surface
/// as an updater that never finds anything, which looks exactly like being up to date -
/// there is no failure to notice, which is why it needs a test rather than a try.
/// </remarks>
public class UpdateTests
{
    // ---- Reading a version -------------------------------------------------

    [Theory]
    [InlineData("0.3.0", 0, 3, 0)]
    [InlineData("v0.3.0", 0, 3, 0)]
    [InlineData("v1.12.4", 1, 12, 4)]
    [InlineData("0.3.0+da2a437", 0, 3, 0)]
    [InlineData("0.4.0-rc1", 0, 4, 0)]
    [InlineData("  v2.0.1  ", 2, 0, 1)]
    public void Parses_a_version_out_of_whatever_a_tag_calls_itself(
        string text, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), AppVersion.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData(null)]
    public void Reports_no_version_rather_than_guessing_at_one(string? text)
    {
        Assert.Null(AppVersion.Parse(text));
    }

    /// <summary>
    /// Version stores an absent field as -1 and sorts it below zero, so "0.3.0" and
    /// "0.3.0.0" would compare as different releases and the second would look newer
    /// forever. Both spellings have to land on the same three-part number.
    /// </summary>
    [Fact]
    public void Two_spellings_of_one_release_are_one_version()
    {
        Assert.Equal(AppVersion.Parse("0.3.0"), AppVersion.Parse("0.3.0.0"));
        Assert.False(AppVersion.Parse("0.3.0.0") > AppVersion.Parse("0.3.0"));
    }

    [Fact]
    public void Only_a_higher_version_is_an_update()
    {
        Assert.False(AppVersion.IsNewerThanCurrent(AppVersion.Current));
        Assert.False(AppVersion.IsNewerThanCurrent(new Version(0, 0, 1)));
        Assert.False(AppVersion.IsNewerThanCurrent(null));
        Assert.True(AppVersion.IsNewerThanCurrent(new Version(999, 0, 0)));
    }

    // ---- Which package this copy came from ---------------------------------

    [Fact]
    public void An_app_bundle_is_recognised_by_its_layout_and_names_the_bundle()
    {
        var location = InstallDetection.DetectMac("/Applications/Omnigit.app/Contents/MacOS/Omnigit");

        Assert.Equal(InstallMedium.MacAppBundle, location.Medium);

        // The bundle, not the executable's directory: Info.plist, the natives and the
        // signature only make sense replaced together.
        Assert.Equal("/Applications/Omnigit.app", location.Target);
    }

    [Fact]
    public void A_loose_binary_on_macos_is_not_mistaken_for_a_bundle()
    {
        Assert.Equal(
            InstallMedium.Unknown,
            InstallDetection.DetectMac("/Users/someone/build/Omnigit").Medium);
    }

    /// <summary>
    /// build/linux/package.sh installs the payload under /usr/lib/omnigit for both the
    /// .deb and the .rpm, so which one it is comes from the system rather than the path.
    /// </summary>
    [Fact]
    public void A_binary_under_usr_belongs_to_a_package_manager()
    {
        var location = InstallDetection.DetectLinux("/usr/lib/omnigit/Omnigit");

        // Whichever this machine runs; the test asserts it is one of them rather than
        // guessing which distribution is running it.
        Assert.Contains(
            location.Medium,
            new[] { InstallMedium.DebPackage, InstallMedium.RpmPackage, InstallMedium.Unknown });

        // Installing over it needs a root prompt, and the user should be told first.
        if (location.Medium is not InstallMedium.Unknown)
            Assert.True(location.NeedsElevation);
    }

    [Fact]
    public void A_binary_anywhere_else_on_linux_is_the_portable_build()
    {
        var location = InstallDetection.DetectLinux("/home/someone/omnigit/Omnigit");

        Assert.Equal(InstallMedium.LinuxTarball, location.Medium);
        Assert.Equal("/home/someone/omnigit", location.Target);

        // Nothing about a tarball needs a password: it is all inside one directory the
        // user owns.
        Assert.False(location.NeedsElevation);
    }

    /// <summary>
    /// Inno Setup writes unins000.exe into the directory it installed to, so its
    /// presence is the same question as "did the installer put this here" - and unlike
    /// the uninstall registry key it needs no guess about which hive to look in.
    /// </summary>
    [Fact]
    public void Windows_tells_an_installed_copy_from_an_unzipped_one_by_the_uninstaller()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);

        try
        {
            var exe = Path.Combine(directory, "Omnigit.exe");
            File.WriteAllText(exe, string.Empty);

            Assert.Equal(InstallMedium.WindowsPortable, InstallDetection.DetectWindows(exe).Medium);

            File.WriteAllText(Path.Combine(directory, "unins000.exe"), string.Empty);

            Assert.Equal(InstallMedium.WindowsInstaller, InstallDetection.DetectWindows(exe).Medium);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Everything but the Flatpak, which cannot - its sandbox grants no way to reach the
    /// host's flatpak, and that is a design decision of the sandbox rather than a
    /// permission we could ask for.
    /// </summary>
    [Fact]
    public void Every_medium_but_the_flatpak_updates_itself()
    {
        foreach (var medium in Enum.GetValues<InstallMedium>())
        {
            var location = new InstallLocation(medium, "/somewhere");
            var expected = medium is not (InstallMedium.Flatpak or InstallMedium.Unknown);

            Assert.Equal(expected, location.CanSelfUpdate);
        }

        Assert.NotNull(new InstallLocation(InstallMedium.Flatpak, null).ManagedBy);
    }

    /// <summary>
    /// The launch path can differ from what was replaced: a package installs its payload
    /// in one place and the launcher the desktop entry names in another.
    /// </summary>
    [Fact]
    public void What_is_replaced_and_what_is_started_can_differ()
    {
        var package = new InstallLocation(InstallMedium.DebPackage, "/usr/lib/omnigit", "/usr/bin/omnigit");

        Assert.Equal("/usr/lib/omnigit", package.Target);
        Assert.Equal("/usr/bin/omnigit", package.Executable);

        // With nothing else said, the thing replaced is the thing started.
        var appImage = new InstallLocation(InstallMedium.AppImage, "/home/someone/Omnigit.AppImage");
        Assert.Equal(appImage.Target, appImage.Executable);
    }

    /// <summary>An AppImage whose file has gone is not something to write over.</summary>
    [Fact]
    public void An_appimage_with_no_path_cannot_update_itself()
    {
        Assert.False(new InstallLocation(InstallMedium.AppImage, null).CanSelfUpdate);
    }

    // ---- Naming the asset to download --------------------------------------

    /// <summary>
    /// Held against the script that names the artifacts, not against a copy of its
    /// output. Parsed rather than run so this passes on a machine with no bash.
    /// </summary>
    [Fact]
    public void The_appimage_it_would_download_is_one_a_release_actually_contains()
    {
        var version = new Version(1, 2, 3);
        var wanted = UpdateService.AssetNameFor(InstallMedium.AppImage, version);

        Assert.NotNull(wanted);
        Assert.Contains(wanted, ArtifactNames(version));
    }

    /// <summary>
    /// The .dmg is the one artifact no test machine can exercise, so the name is the
    /// only thing that can be checked - and a wrong one is an updater that reports
    /// "still up to date" on every Mac forever.
    /// </summary>
    [Fact]
    public void The_disk_image_is_named_for_the_runtime_identifier()
    {
        var name = UpdateService.AssetNameFor(InstallMedium.MacAppBundle, new Version(1, 2, 3));

        Assert.NotNull(name);
        Assert.EndsWith(".dmg", name);
        Assert.Contains(name, ArtifactNames(new Version(1, 2, 3)));
    }

    /// <summary>
    /// Every medium that offers the button has to name a file a release actually
    /// contains, and each packaging tool spells the architecture its own way - the
    /// kernel's, Debian's, the RPM world's and .NET's, four spellings of one machine.
    /// </summary>
    [Fact]
    public void Every_updatable_medium_names_an_artifact_a_release_contains()
    {
        var version = new Version(1, 2, 3);
        var artifacts = ArtifactNames(version);

        foreach (var medium in Enum.GetValues<InstallMedium>())
        {
            var name = UpdateService.AssetNameFor(medium, version);

            if (!new InstallLocation(medium, "/somewhere").CanSelfUpdate)
            {
                Assert.Null(name);
                continue;
            }

            Assert.NotNull(name);
            Assert.Contains(name, artifacts);
        }
    }

    /// <summary>Every filename build/expected-artifacts.sh says a release contains.</summary>
    private static IReadOnlyList<string> ArtifactNames(Version version)
    {
        var script = File.ReadAllText(RepositoryFile("build/expected-artifacts.sh"));

        // The script's echo lines are its whole output; $1 only ever holds the flatpak
        // architecture, which is expanded here the same way the case block does.
        var names = Regex.Matches(script, @"echo ""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Where(name => name.Contains("$VERSION") || name.Contains("${VERSION}"))
            .SelectMany(name => new[] { name.Replace("$1", "x86_64"), name.Replace("$1", "aarch64") })
            .Select(name => name
                .Replace("${VERSION}", version.ToString(3))
                .Replace("$VERSION", version.ToString(3)))
            .Distinct()
            .ToList();

        // If the regex ever stops matching, every assertion above passes vacuously.
        Assert.NotEmpty(names);
        return names;
    }

    // ---- Reading the release feed ------------------------------------------

    [Fact]
    public async Task A_higher_version_on_the_feed_is_an_update()
    {
        var result = await Check(ReleaseJson("v99.0.0"));

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal(new Version(99, 0, 0), result.Release!.Version);
        Assert.Equal("v99.0.0", result.Release.Tag);
    }

    [Fact]
    public async Task The_version_this_copy_already_is_is_not_an_update()
    {
        var result = await Check(ReleaseJson("v" + AppVersion.Display));

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task The_assets_come_back_with_the_release()
    {
        var result = await Check(ReleaseJson("v99.0.0"));

        var asset = Assert.Single(
            result.Release!.Assets,
            a => a.Name == "Omnigit-99.0.0-x86_64.AppImage");

        Assert.Equal(81_000_000, asset.Size);
        Assert.Equal("https://example.invalid/Omnigit-99.0.0-x86_64.AppImage", asset.Url.ToString());
    }

    /// <summary>
    /// Being unable to reach GitHub is ordinary - the app works offline - so it comes
    /// back as an outcome rather than as an exception the caller has to catch.
    /// </summary>
    [Fact]
    public async Task An_unreachable_feed_is_a_result_not_a_throw()
    {
        var result = await Check("nope", HttpStatusCode.ServiceUnavailable);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public async Task A_release_whose_tag_holds_no_version_is_not_offered()
    {
        var result = await Check("""{ "tag_name": "nightly", "assets": [] }""");

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
    }

    // ---- The parts of a release body and a checksum manifest ----------------

    /// <summary>
    /// release.yml builds the body as the metainfo's notes followed by the standing
    /// install sections. Only the first part is news; the rest tells the reader how to
    /// download a file they already have, inside an app about to replace it for them.
    /// </summary>
    [Fact]
    public void Only_the_part_of_a_release_body_that_says_what_changed_is_kept()
    {
        var summary = UpdateService.Summarise("""
            The branch picker gained a filter box.

            ## Install

            **Linux** - the .deb, the .rpm, or the AppImage.

            ## Getting started

            Add a clone you already have.
            """);

        Assert.Equal("The branch picker gained a filter box.", summary);
    }

    [Fact]
    public void A_body_that_is_all_boilerplate_summarises_to_nothing()
    {
        Assert.Equal(string.Empty, UpdateService.Summarise("## Install\n\nRun the setup."));
        Assert.Equal(string.Empty, UpdateService.Summarise(null));
    }

    [Fact]
    public void Reads_a_sha256sum_manifest()
    {
        var checksums = UpdateService.ParseChecksums("""
            87428fc522803d31065e7bce3cf03fe475096631e5e07bbd7a0fde60c4cf25c7  Omnigit-1.2.3-x86_64.AppImage
            0263829989b6fd954f72baaf2fc64bc2e2f01d692d4de72986ea808f6e99813f *omnigit_1.2.3_amd64.deb

            not a checksum line
            """);

        Assert.Equal(2, checksums.Count);
        Assert.Equal(
            "87428fc522803d31065e7bce3cf03fe475096631e5e07bbd7a0fde60c4cf25c7",
            checksums["Omnigit-1.2.3-x86_64.AppImage"]);

        // sha256sum marks a file read in binary mode with a leading star; the star is
        // not part of the name.
        Assert.True(checksums.ContainsKey("omnigit_1.2.3_amd64.deb"));
    }

    // ---- Replacing the running AppImage ------------------------------------

    /// <summary>
    /// The whole step-5 path against real files: fetch the asset, check it against the
    /// published hash, and put it where the running copy is.
    /// </summary>
    [Fact]
    public async Task An_appimage_is_replaced_in_place_by_the_download()
    {
        using var install = new FakeAppImage("the old Omnigit");

        var result = await install.ApplyAsync("the new Omnigit");

        Assert.Equal(UpdateApplyOutcome.Applied, result.Outcome);
        Assert.Equal("the new Omnigit", File.ReadAllText(install.AppImagePath));

        // Same path, so the desktop entry, any dock pin and any symlink still name it.
        Assert.Equal(install.AppImagePath, result.Detail);
    }

    [Fact]
    public async Task The_replacement_is_left_executable()
    {
        using var install = new FakeAppImage("old");

        await install.ApplyAsync("new");

        // The mode is only set off Windows, which is right - an AppImage exists nowhere
        // else. CI runs this on Linux; the guard is for someone running the suite on a
        // Windows machine, where there is nothing here to assert.
        if (OperatingSystem.IsWindows())
            return;

        // An AppImage that is not executable is a file the desktop cannot open, and
        // nothing downstream would report why.
        Assert.True(File.GetUnixFileMode(install.AppImagePath).HasFlag(UnixFileMode.UserExecute));
    }

    /// <summary>
    /// A download whose hash does not match is a truncated transfer or a swapped file.
    /// Either way what is already on disk is the only working Omnigit the user has.
    /// </summary>
    [Fact]
    public async Task A_download_that_does_not_match_its_hash_is_discarded()
    {
        using var install = new FakeAppImage("the old Omnigit");

        var result = await install.ApplyAsync("the new Omnigit", publishedHash: new string('a', 64));

        Assert.Equal(UpdateApplyOutcome.Failed, result.Outcome);
        Assert.Equal("the old Omnigit", File.ReadAllText(install.AppImagePath));
        Assert.Empty(install.StrayFiles);
    }

    [Fact]
    public async Task A_failed_download_leaves_nothing_behind()
    {
        using var install = new FakeAppImage("the old Omnigit");

        var result = await install.ApplyAsync("irrelevant", status: HttpStatusCode.NotFound);

        Assert.Equal(UpdateApplyOutcome.Failed, result.Outcome);
        Assert.Equal("the old Omnigit", File.ReadAllText(install.AppImagePath));

        // Half an Omnigit next to the real one is worse than none: the next attempt
        // would find a file where it wanted to write one.
        Assert.Empty(install.StrayFiles);
    }

    [Fact]
    public async Task A_release_missing_the_checksum_manifest_installs_nothing()
    {
        using var install = new FakeAppImage("the old Omnigit");

        var result = await install.ApplyAsync("the new Omnigit", publishChecksums: false);

        Assert.Equal(UpdateApplyOutcome.Failed, result.Outcome);
        Assert.Equal("the old Omnigit", File.ReadAllText(install.AppImagePath));
    }

    [Fact]
    public async Task Progress_runs_to_completion()
    {
        using var install = new FakeAppImage("old");

        var reported = new List<UpdateProgress>();
        await install.ApplyAsync("new", progress: new Progress<UpdateProgress>(reported.Add));

        // Progress<T> posts to the synchronisation context, so the last report can still
        // be in flight; what matters is that it got there.
        await Task.Delay(50);

        Assert.NotEmpty(reported);
        Assert.Equal(1d, reported[^1].Fraction, precision: 3);
    }

    /// <summary>
    /// The two halves have to be told apart, because for a package install the second is
    /// usually a password box that may have opened on another screen. Reporting
    /// "Downloading" through it is how a wait for the user reads as a hang - which is
    /// exactly what it did the first time this ran for real.
    /// </summary>
    [Fact]
    public async Task Installing_is_reported_separately_from_downloading()
    {
        using var install = new FakeAppImage("old");

        var reported = new List<UpdateProgress>();
        await install.ApplyAsync("new", progress: new Progress<UpdateProgress>(reported.Add));
        await Task.Delay(50);

        Assert.Contains(reported, r => r.Phase == UpdatePhase.Downloading);
        Assert.Contains(reported, r => r.Phase == UpdatePhase.Installing);

        // And in that order - a label that goes back to "Downloading" is worse than one
        // that never changed.
        var download = reported.FindLastIndex(r => r.Phase == UpdatePhase.Downloading);
        var installing = reported.FindIndex(r => r.Phase == UpdatePhase.Installing);

        Assert.True(installing > download, "installing should be reported after downloading");
    }

    [Fact]
    public async Task An_install_something_else_updates_is_not_touched()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK, ""));
        var service = new UpdateService(
            http,
            new Uri("https://api.example.invalid/"),
            new InstallLocation(InstallMedium.Flatpak, null));

        var release = new ReleaseInfo(
            new Version(99, 0, 0), "v99.0.0", "", new Uri("https://example.invalid/"), []);

        var result = await service.ApplyAsync(release);

        Assert.Equal(UpdateApplyOutcome.NotSupported, result.Outcome);
        Assert.NotNull(result.Detail);
    }

    // ---- Helpers -----------------------------------------------------------

    private static Task<UpdateCheckResult> Check(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var http = new HttpClient(new StubHandler(status, body));
        var service = new UpdateService(
            http,
            new Uri("https://api.example.invalid/"),
            new InstallLocation(InstallMedium.AppImage, "/home/someone/Omnigit.AppImage"));

        return service.CheckAsync();
    }

    private static string ReleaseJson(string tag) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/Polemus/Omnigit/releases/tag/{{tag}}",
          "body": "What changed.\n\n## Install\n\nRun it.",
          "assets": [
            {
              "name": "Omnigit-99.0.0-x86_64.AppImage",
              "browser_download_url": "https://example.invalid/Omnigit-99.0.0-x86_64.AppImage",
              "size": 81000000
            },
            {
              "name": "SHA256SUMS",
              "browser_download_url": "https://example.invalid/SHA256SUMS",
              "size": 900
            }
          ]
        }
        """;

    /// <summary>
    /// The repository's own copy of a build script, found by walking up from the test
    /// binary - the same trick the manifest tests use to read the manifests we ship
    /// rather than a copy of them.
    /// </summary>
    private static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relative)))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, relative);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    /// <summary>
    /// A directory holding one file standing in for the running .AppImage, and a stub
    /// GitHub serving a release that would replace it.
    /// </summary>
    private sealed class FakeAppImage : IDisposable
    {
        private readonly string _directory;

        public FakeAppImage(string contents)
        {
            _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_directory);

            AppImagePath = Path.Combine(_directory, "Omnigit-1.2.3-x86_64.AppImage");
            File.WriteAllText(AppImagePath, contents);
        }

        /// <summary>Where the "installed" AppImage is.</summary>
        public string AppImagePath { get; }

        /// <summary>Anything in the directory that is not the AppImage itself.</summary>
        public IReadOnlyList<string> StrayFiles =>
            Directory.EnumerateFileSystemEntries(_directory)
                .Where(entry => entry != AppImagePath)
                .ToList();

        public Task<UpdateApplyResult> ApplyAsync(
            string newContents,
            string? publishedHash = null,
            bool publishChecksums = true,
            HttpStatusCode status = HttpStatusCode.OK,
            IProgress<UpdateProgress>? progress = null)
        {
            var name = "Omnigit-99.0.0-x86_64.AppImage";
            var bytes = System.Text.Encoding.UTF8.GetBytes(newContents);
            var hash = publishedHash
                ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

            var assets = new List<ReleaseAsset>
            {
                new(name, new Uri($"https://example.invalid/{name}"), bytes.Length),
            };

            if (publishChecksums)
                assets.Add(new("SHA256SUMS", new Uri("https://example.invalid/SHA256SUMS"), 90));

            var handler = new ReleaseHandler(name, bytes, $"{hash}  {name}\n", status);
            var service = new UpdateService(
                new HttpClient(handler),
                new Uri("https://api.example.invalid/"),
                new InstallLocation(InstallMedium.AppImage, AppImagePath));

            var release = new ReleaseInfo(
                new Version(99, 0, 0), "v99.0.0", "", new Uri("https://example.invalid/"), assets);

            return service.ApplyAsync(release, progress);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Serves the checksum manifest and the asset, and nothing else.</summary>
    private sealed class ReleaseHandler(
        string assetName, byte[] asset, string checksums, HttpStatusCode assetStatus) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("SHA256SUMS", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(checksums),
                });
            }

            if (path.EndsWith(assetName, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(assetStatus)
                {
                    Content = new ByteArrayContent(assetStatus == HttpStatusCode.OK ? asset : []),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
