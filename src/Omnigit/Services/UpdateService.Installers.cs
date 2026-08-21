using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnigit.Services;

/// <summary>
/// What each kind of install does with the file once it has been downloaded and its
/// hash checked. The download is shared; only this differs.
/// </summary>
/// <remarks>
/// Two rules hold across all of them, and both are about the same thing - never leaving
/// the user with no working Omnigit.
///
/// **Nothing is destroyed before the replacement is proven.** The hash is checked before
/// anything is moved, and where a swap cannot be atomic the old copy is kept under
/// another name until the new one is in place.
///
/// **Whatever is thrown away is thrown away last.** A failure halfway through leaves the
/// old version running and installed, and says why.
/// </remarks>
public sealed partial class UpdateService
{
    /// <summary>Where a download goes when it does not need to be beside its target.</summary>
    private static string StagingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnigit-update");
        Directory.CreateDirectory(directory);
        return directory;
    }

    // ---- .deb and .rpm -----------------------------------------------------

    /// <summary>
    /// Hands the downloaded package to the distribution's own installer, through pkexec.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>dpkg -i</c> or <c>rpm -U</c>: those install a file and fail on
    /// a dependency they cannot resolve, where apt and dnf fetch it. The upgrade is then
    /// exactly the one the user would have got from a repository, which is what makes
    /// this safe to do to a system-managed install at all - the package database stays
    /// correct, and a later <c>apt upgrade</c> is not confused by what happened here.
    ///
    /// pkexec rather than sudo: it asks through the desktop's polkit agent, which is a
    /// dialog the user recognises, and sudo in a GUI process has no terminal to ask on.
    ///
    /// The package is left in place afterwards. It is the evidence for what happened,
    /// and the temp directory is cleared by the system in its own time.
    /// </remarks>
    private async Task<UpdateApplyResult> InstallSystemPackageAsync(
        ReleaseAsset asset,
        string expected,
        IProgress<double>? progress,
        CancellationToken cancel)
    {
        var package = Path.Combine(StagingDirectory(), asset.Name);

        var actual = await DownloadAsync(asset, package, progress, cancel).ConfigureAwait(false);
        if (!Matches(actual, expected))
        {
            Delete(package);
            return Mismatched(asset);
        }

        if (PackageInstallerFor(Location.Medium, package) is not { } command)
        {
            return new(
                UpdateApplyOutcome.Failed,
                "No supported package manager was found to install it with.");
        }

        var (program, arguments) = command;
        var run = await RunAsync("/usr/bin/pkexec", [program, .. arguments], cancel)
            .ConfigureAwait(false);

        if (run.ExitCode != 0)
        {
            // 126 is polkit's own: the dialog was dismissed, or the user is not
            // authorised. Saying "cancelled" beats reporting a number.
            var why = run.ExitCode == 126
                ? "The password prompt was dismissed."
                : Summary(run) ?? $"{program} exited {run.ExitCode}.";

            return new(UpdateApplyOutcome.Failed, why);
        }

        return new(UpdateApplyOutcome.Applied, package);
    }

    /// <summary>
    /// The command that installs a downloaded package on this system, or null if none of
    /// the ones we know about is present.
    /// </summary>
    /// <remarks>
    /// <c>--allow-downgrades</c> and dnf's <c>--best</c> are absent on purpose: this only
    /// ever installs a version the app has already established is newer.
    /// </remarks>
    private static (string Program, string[] Arguments)? PackageInstallerFor(
        InstallMedium medium, string package)
    {
        // A path with no slash in it would be read as a package name from the repository
        // rather than as a file, which is a different package entirely.
        var path = Path.GetFullPath(package);

        return medium switch
        {
            InstallMedium.DebPackage when File.Exists("/usr/bin/apt-get") =>
                ("/usr/bin/apt-get", ["install", "-y", path]),

            InstallMedium.RpmPackage when File.Exists("/usr/bin/dnf") =>
                ("/usr/bin/dnf", ["install", "-y", path]),

            InstallMedium.RpmPackage when File.Exists("/usr/bin/zypper") =>
                ("/usr/bin/zypper", ["--non-interactive", "install", "--allow-unsigned-rpm", path]),

            _ => null,
        };
    }

    // ---- The Windows installer ---------------------------------------------

    /// <summary>
    /// Starts the downloaded Inno installer over the top of this one and leaves.
    /// </summary>
    /// <remarks>
    /// The installer does the whole job, closing Omnigit and starting it again: the
    /// AppId matches so it upgrades in place, and CloseApplications shuts down whatever
    /// holds files in the install directory - this process. Waiting would be waiting to
    /// be killed.
    ///
    /// **/ALLUSERS or /CURRENTUSER is not optional**: omnigit.iss sets
    /// PrivilegesRequiredOverridesAllowed=dialog, and under /SILENT Inno cannot draw
    /// that dialog and waits forever. The scope is read from where this copy is, so a
    /// per-user install never asks for an administrator. /RELAUNCH=1 is ours - see the
    /// [Run] entry, which exists because the stock one carries skipifsilent.
    /// </remarks>
    private async Task<UpdateApplyResult> RunWindowsInstallerAsync(
        string target,
        ReleaseAsset asset,
        string expected,
        IProgress<double>? progress,
        CancellationToken cancel)
    {
        var installer = Path.Combine(StagingDirectory(), asset.Name);

        var actual = await DownloadAsync(asset, installer, progress, cancel).ConfigureAwait(false);
        if (!Matches(actual, expected))
        {
            Delete(installer);
            return Mismatched(asset);
        }

        var scope = IsPerMachine(target) ? "/ALLUSERS" : "/CURRENTUSER";

        if (!Spawn(installer, ["/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", scope, "/RELAUNCH=1"]))
            return new(UpdateApplyOutcome.Failed, $"Could not start {asset.Name}.");

        return new(UpdateApplyOutcome.Applied, target);
    }

    /// <summary>
    /// Whether this copy was installed for everyone, judged by where it sits.
    /// </summary>
    /// <remarks>
    /// The registry would answer too, but only if you already know which hive to look
    /// in - which is the same question. The directory is the answer either way, and
    /// getting it wrong means Inno installing a second copy in the other scope rather
    /// than upgrading this one.
    /// </remarks>
    private static bool IsPerMachine(string directory)
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root)
                && directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ---- The portable builds -----------------------------------------------

    /// <summary>
    /// Unpacks the archive beside the installation and swaps the two directories.
    /// </summary>
    /// <remarks>
    /// Three renames rather than a copy over the top: unpack to <c>.new</c>, move the
    /// installation to <c>.old</c>, move <c>.new</c> into place. A copy over a live
    /// directory interrupted halfway leaves half of one version and half of another,
    /// which starts and then fails somewhere unrelated. Renames within one directory are
    /// atomic and the worst interruption leaves a directory beside the real one.
    ///
    /// The old directory is removed last and its failure is not the update's failure -
    /// on Windows the running executable still lives in it, so it cannot be deleted
    /// until this process ends. It is left for the next launch to clear, which is what
    /// SweepPreviousVersion does.
    /// </remarks>
    private async Task<UpdateApplyResult> ReplaceDirectoryAsync(
        string target,
        ReleaseAsset asset,
        string expected,
        IProgress<double>? progress,
        CancellationToken cancel)
    {
        var parent = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(parent))
            return new(UpdateApplyOutcome.Failed, $"cannot tell what {target} sits inside");

        var archive = Path.Combine(StagingDirectory(), asset.Name);
        var staged = target + ".new";
        var previous = target + ".old";

        try
        {
            var actual = await DownloadAsync(asset, archive, progress, cancel).ConfigureAwait(false);
            if (!Matches(actual, expected))
                return Mismatched(asset);

            if (Directory.Exists(staged))
                Directory.Delete(staged, recursive: true);

            await ExtractAsync(archive, staged, cancel).ConfigureAwait(false);

            // The Linux tarball wraps everything in an "omnigit" directory; the Windows
            // zip does not. Unwrapping a lone directory makes both land the same way.
            Unwrap(staged);

            if (!File.Exists(Path.Combine(staged, ExecutableName())))
            {
                Directory.Delete(staged, recursive: true);
                return new(UpdateApplyOutcome.Failed, $"{asset.Name} does not contain Omnigit.");
            }

            if (Directory.Exists(previous))
                TryDeleteDirectory(previous);

            // Windows cannot do the swap from in here at all - see HandOverToHelper.
            if (OperatingSystem.IsWindows())
                return HandOverToHelper(target, staged, previous);

            Directory.Move(target, previous);

            try
            {
                Directory.Move(staged, target);
            }
            catch
            {
                // Put it back rather than leaving nothing where Omnigit was.
                Directory.Move(previous, target);
                throw;
            }

            MakeExecutable(Path.Combine(target, ExecutableName()));
            return new(UpdateApplyOutcome.Applied, target);
        }
        finally
        {
            Delete(archive);
        }
    }

    /// <summary>
    /// Hands the directory swap to a script that runs after Omnigit has exited.
    /// </summary>
    /// <remarks>
    /// **Windows will not rename a directory holding files the calling process has
    /// open.** Renaming a running .exe is allowed; the two hundred assemblies beside it
    /// are not opened with FILE_SHARE_DELETE, and one is enough to lock the directory.
    /// It surfaces as <c>Access to the path '...' is denied</c>, which reads like a
    /// permissions problem and is not one - elevation changes nothing.
    ///
    /// So the swap cannot happen while Omnigit runs, and Omnigit cannot outlive its own
    /// exit to do it after. The helper waits on this process id, swaps, starts the new
    /// copy and deletes itself. Linux keeps the direct rename above, which does not care
    /// what is open.
    /// </remarks>
    private static UpdateApplyResult HandOverToHelper(string target, string staged, string previous)
    {
        var script = Path.Combine(StagingDirectory(), "omnigit-swap.ps1");
        var executable = Path.Combine(target, ExecutableName());

        // Written as a script rather than passed as -Command: the paths contain spaces
        // and quoting them through a command line that PowerShell re-parses is a way to
        // lose an argument silently.
        // $$ so the interpolation holes are {{...}} and PowerShell's own braces can stay
        // single, which keeps the script readable as the script it is.
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Stop'

            # Long enough for a slow shutdown, short enough that a hung Omnigit does not
            # leave a script waiting for ever. If it is still running, do nothing at all:
            # a half-done swap is worse than no swap.
            try { Wait-Process -Id {{Environment.ProcessId}} -Timeout 120 -ErrorAction Stop }
            catch [System.TimeoutException] { exit 1 }
            catch { }

            # The handles go with the process, but Windows releases them lazily.
            Start-Sleep -Milliseconds 500

            Move-Item -LiteralPath '{{target}}' -Destination '{{previous}}' -Force
            try {
                Move-Item -LiteralPath '{{staged}}' -Destination '{{target}}' -Force
            } catch {
                Move-Item -LiteralPath '{{previous}}' -Destination '{{target}}' -Force
                exit 1
            }

            Start-Process -FilePath '{{executable}}'

            # Best effort, and last: the update has already succeeded by here.
            Remove-Item -LiteralPath '{{previous}}' -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath '{{script}}' -Force -ErrorAction SilentlyContinue
            """);

        var started = Spawn("powershell.exe",
        [
            "-NoProfile",
            "-NonInteractive",
            // The user did not choose to run a script and should not have to allow one.
            "-ExecutionPolicy", "Bypass",
            "-WindowStyle", "Hidden",
            "-File", script,
        ]);

        if (!started)
            return new(UpdateApplyOutcome.Failed, "Could not start the helper that completes the update.");

        return new(UpdateApplyOutcome.Applied, target);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The file name of the app itself, which is all the platforms differ by.</summary>
    private static string ExecutableName() => OperatingSystem.IsWindows() ? "Omnigit.exe" : "Omnigit";

    /// <summary>
    /// Removes the <c>.old</c> directory a previous update left behind.
    /// </summary>
    /// <remarks>
    /// Called at startup rather than after the swap, because on Windows the directory
    /// holds the executable this process was running from and cannot be deleted until
    /// that process has ended. Failing is fine and silent - it is reclaimed disk, and
    /// the next launch will try again.
    /// </remarks>
    public static void SweepPreviousVersion()
    {
        var location = InstallDetection.Detect();

        if (location.Medium is not (InstallMedium.WindowsPortable or InstallMedium.LinuxTarball))
            return;

        if (location.Target is not { } target || !Directory.Exists(target + ".old"))
            return;

        try
        {
            Directory.Delete(target + ".old", recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static Task ExtractAsync(string archive, string destination, CancellationToken cancel)
    {
        Directory.CreateDirectory(destination);

        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Task.Run(
                () => ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true),
                cancel);
        }

        // TarFile reads the entries; the gzip layer is peeled off first because a
        // .tar.gz is exactly that and nothing in the framework does both at once.
        return Task.Run(async () =>
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: true, cancel)
                .ConfigureAwait(false);
        }, cancel);
    }

    /// <summary>Flattens a directory holding nothing but one directory.</summary>
    private static void Unwrap(string directory)
    {
        var entries = Directory.GetFileSystemEntries(directory);
        if (entries.Length != 1 || !Directory.Exists(entries[0]))
            return;

        var inner = entries[0];
        foreach (var entry in Directory.GetFileSystemEntries(inner))
            Directory.Move(entry, Path.Combine(directory, Path.GetFileName(entry)));

        Directory.Delete(inner, recursive: true);
    }

    // ---- The macOS bundle --------------------------------------------------

    /// <summary>
    /// Mounts the disk image and puts the bundle inside it where this one is.
    /// </summary>
    /// <remarks>
    /// **Untested. There is no Mac to run it on** - the same gap the credential backend
    /// has. Written to the documented behaviour of hdiutil and ditto, and the parts most
    /// likely to be wrong are called out below.
    ///
    /// ditto rather than cp or Directory.Move: it is the only copy that preserves
    /// extended attributes and resource forks, and a bundle copied without them loses
    /// its code signature - which is the difference between an app that opens and one
    /// Gatekeeper refuses. The same reason build/macos/package.sh uses it.
    ///
    /// The image is detached in a finally block. A mount left behind is a volume on the
    /// user's desktop that they did not ask for and cannot explain.
    /// </remarks>
    private async Task<UpdateApplyResult> ReplaceAppBundleAsync(
        string target,
        ReleaseAsset asset,
        string expected,
        IProgress<double>? progress,
        CancellationToken cancel)
    {
        var image = Path.Combine(StagingDirectory(), asset.Name);
        var mount = Path.Combine(StagingDirectory(), "mount-" + Guid.NewGuid().ToString("N")[..8]);
        var mounted = false;

        try
        {
            var actual = await DownloadAsync(asset, image, progress, cancel).ConfigureAwait(false);
            if (!Matches(actual, expected))
                return Mismatched(asset);

            // -nobrowse keeps it off the desktop and out of the Finder sidebar; this is
            // a mount the user never asked for and should never see.
            var attach = await RunAsync(
                "/usr/bin/hdiutil",
                ["attach", image, "-nobrowse", "-quiet", "-mountpoint", mount],
                cancel).ConfigureAwait(false);

            if (attach.ExitCode != 0)
                return new(UpdateApplyOutcome.Failed, Summary(attach) ?? "Could not open the disk image.");

            mounted = true;

            var bundle = Directory.EnumerateDirectories(mount, "*.app").FirstOrDefault();
            if (bundle is null)
                return new(UpdateApplyOutcome.Failed, $"{asset.Name} contains no application.");

            // Into a sibling first, so a copy that fails partway has not touched the
            // installed bundle. Only then are the two swapped.
            var staged = target + ".new";
            if (Directory.Exists(staged))
                Directory.Delete(staged, recursive: true);

            var copy = await RunAsync("/usr/bin/ditto", [bundle, staged], cancel).ConfigureAwait(false);
            if (copy.ExitCode != 0)
                return new(UpdateApplyOutcome.Failed, Summary(copy) ?? "Could not copy the application.");

            var previous = target + ".old";
            if (Directory.Exists(previous))
                Directory.Delete(previous, recursive: true);

            Directory.Move(target, previous);

            try
            {
                Directory.Move(staged, target);
            }
            catch
            {
                Directory.Move(previous, target);
                throw;
            }

            // Unlike the portable builds this one can go now: the running process lives
            // on its own copy of the executable, and macOS keeps that alive by inode.
            try
            {
                Directory.Delete(previous, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return new(UpdateApplyOutcome.Applied, target);
        }
        finally
        {
            if (mounted)
                _ = await RunAsync("/usr/bin/hdiutil", ["detach", mount, "-quiet"], cancel)
                    .ConfigureAwait(false);

            Delete(image);
        }
    }

    // ---- Running things ----------------------------------------------------

    private readonly record struct RunResult(int ExitCode, string Output, string Error);

    /// <summary>Runs a program to completion and collects what it said.</summary>
    private static async Task<RunResult> RunAsync(
        string program, IReadOnlyList<string> arguments, CancellationToken cancel)
    {
        var info = new ProcessStartInfo
        {
            FileName = program,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info);
        if (process is null)
            return new(-1, string.Empty, $"could not start {program}");

        // Both streams are read before waiting: a program that fills a pipe while
        // nobody drains it blocks, and then so does the wait.
        var output = process.StandardOutput.ReadToEndAsync(cancel);
        var error = process.StandardError.ReadToEndAsync(cancel);

        await process.WaitForExitAsync(cancel).ConfigureAwait(false);

        return new(
            process.ExitCode,
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false));
    }

    /// <summary>Starts a program and does not wait for it.</summary>
    private static bool Spawn(string program, IReadOnlyList<string> arguments)
    {
        try
        {
            CloseDescriptorsOnExec();

            var info = new ProcessStartInfo { FileName = program, UseShellExecute = false };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var started = Process.Start(info);
            return started is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    /// <summary>The last thing a failing program said, for the message under the button.</summary>
    private static string? Summary(RunResult run)
    {
        var text = string.IsNullOrWhiteSpace(run.Error) ? run.Output : run.Error;

        var last = text
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return string.IsNullOrWhiteSpace(last) ? null : last;
    }

    // ---- Shared -------------------------------------------------------------

    private static bool Matches(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static UpdateApplyResult Mismatched(ReleaseAsset asset) => new(
        UpdateApplyOutcome.Failed,
        $"{asset.Name} did not match its published hash and was discarded.");

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
