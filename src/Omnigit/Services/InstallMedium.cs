using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Omnigit.Services;

/// <summary>
/// Which of the fourteen things a release contains this copy was installed from.
/// </summary>
/// <remarks>
/// The updater has to know, because the answer decides both what to download and
/// whether to offer anything at all. Half the formats belong to a package manager
/// that will update them itself and would be actively harmed by us writing over
/// root-owned files; the other half have no owner but us.
/// </remarks>
public enum InstallMedium
{
    /// <summary>Nothing recognised - a `dotnet run`, or a layout we don't ship.</summary>
    Unknown,

    /// <summary>One file, launched directly. Ours to replace.</summary>
    AppImage,

    /// <summary>
    /// Inside the sandbox. Updates come from the remote the user installed from, and
    /// there is no way to reach the host's flatpak from in here - finish-args grants
    /// no session-bus name for it, and granting one would be a sandbox escape.
    /// </summary>
    Flatpak,

    /// <summary>Installed by dpkg under /usr. Updated by handing apt a newer .deb.</summary>
    DebPackage,

    /// <summary>Installed by rpm under /usr. Updated by handing dnf or zypper a newer .rpm.</summary>
    RpmPackage,

    /// <summary>Unpacked from the portable .tar.gz, wherever the user put it.</summary>
    LinuxTarball,

    /// <summary>Installed by the Inno Setup .exe, which can upgrade itself in place.</summary>
    WindowsInstaller,

    /// <summary>Unzipped from the portable .zip.</summary>
    WindowsPortable,

    /// <summary>Omnigit.app, usually in /Applications.</summary>
    MacAppBundle,
}

/// <summary>Where this copy of Omnigit lives, and what can be done about that.</summary>
/// <param name="Medium">Which package it came from.</param>
/// <param name="Target">
/// The thing an update replaces: the .AppImage file, the .app bundle, the installation
/// directory. Null when there is nothing we would touch.
/// </param>
/// <param name="Launch">
/// What to start once the update is in. Usually <paramref name="Target"/> - an AppImage
/// is both - but a package installs its payload in one place and its launcher in
/// another, and starting the payload directly would work while bypassing the launcher
/// the desktop entry names.
/// </param>
public readonly record struct InstallLocation(
    InstallMedium Medium,
    string? Target,
    string? Launch = null)
{
    /// <summary>What to start after updating. Falls back to whatever was replaced.</summary>
    public string? Executable => Launch ?? Target;

    /// <summary>
    /// True where Omnigit can replace itself. False does not mean no updates - a
    /// Flatpak and a winget install both update themselves through something better
    /// than a button - it means the button is not what does it.
    /// </summary>
    public bool CanSelfUpdate => Target is not null && Medium is
        InstallMedium.AppImage or
        InstallMedium.DebPackage or
        InstallMedium.RpmPackage or
        InstallMedium.LinuxTarball or
        InstallMedium.WindowsInstaller or
        InstallMedium.WindowsPortable or
        InstallMedium.MacAppBundle;

    /// <summary>
    /// True where installing needs a password or a UAC prompt, so the UI can say so
    /// before the prompt appears rather than letting one arrive unexplained.
    /// </summary>
    public bool NeedsElevation => Medium switch
    {
        // The distro's own installer writes to /usr, through pkexec.
        InstallMedium.DebPackage or InstallMedium.RpmPackage => true,

        // Inno installing into Program Files. A per-user install does not, but that is
        // decided by where this copy actually is - see InstallDetection.
        InstallMedium.WindowsInstaller => true,

        _ => false,
    };

    /// <summary>
    /// What to tell someone whose copy we cannot replace. Null where there is nothing
    /// to say beyond the version.
    /// </summary>
    public string? ManagedBy => Medium switch
    {
        InstallMedium.Flatpak =>
            "Installed as a Flatpak. Run flatpak update, or let your software centre do it.",
        _ => null,
    };
}

/// <summary>
/// Works out how Omnigit was installed, from the filesystem around it.
/// </summary>
/// <remarks>
/// The three per-platform methods are internal rather than private because each is a
/// claim about a layout only one of the three runners has, and the tests run on one
/// of them - calling them directly is the only way Linux can check the macOS rule.
///
/// Every test here is a fact about the layout a packaging script produced, so each
/// one is paired with the script that guarantees it. Nothing is written down at build
/// time and read back: a build flag would be one more copy of a fact to forget, and
/// it would also be wrong for the tarball, which is the same publish output as the
/// .deb's payload and is told apart only by where it ended up.
/// </remarks>
public static class InstallDetection
{
    /// <summary>Present in every Flatpak sandbox, and in nothing else.</summary>
    private const string FlatpakMarker = "/.flatpak-info";

    /// <summary>Inno Setup writes its uninstaller into the application directory.</summary>
    private const string InnoUninstaller = "unins000.exe";

    public static InstallLocation Detect() => Detect(Environment.ProcessPath);

    internal static InstallLocation Detect(string? processPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return DetectLinux(processPath);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return DetectWindows(processPath);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return DetectMac(processPath);

        return new(InstallMedium.Unknown, null);
    }

    internal static InstallLocation DetectLinux(string? processPath)
    {
        // The AppImage runtime exports APPIMAGE as the file the user launched. This is
        // the same test DesktopIntegration makes, and for the same reason: it is the
        // only way to learn the path of a file that has mounted itself somewhere else.
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrEmpty(appImage) && File.Exists(appImage))
        {
            var full = Path.GetFullPath(appImage);
            return new(InstallMedium.AppImage, full, full);
        }

        if (File.Exists(FlatpakMarker))
            return new(InstallMedium.Flatpak, null);

        var directory = DirectoryOf(processPath);
        if (directory is null)
            return new(InstallMedium.Unknown, null);

        // build/linux/package.sh installs the payload under /usr/lib/omnigit with a
        // launcher symlinked from /usr/bin, for both the .deb and the .rpm. Anywhere
        // else is a tarball the user unpacked, whatever it sits next to.
        if (directory.StartsWith("/usr/", StringComparison.Ordinal))
        {
            // /usr/bin/omnigit rather than the payload: it is what the desktop entry
            // names, and after an upgrade it is the one path certain to exist.
            const string launcher = "/usr/bin/omnigit";
            var launch = File.Exists(launcher) ? launcher : processPath;

            return new(PackageManager(), directory, launch);
        }

        return new(InstallMedium.LinuxTarball, directory, processPath);
    }

    internal static InstallLocation DetectWindows(string? processPath)
    {
        var directory = DirectoryOf(processPath);
        if (directory is null)
            return new(InstallMedium.Unknown, null);

        // Asking the filesystem rather than the registry. The uninstaller is written
        // by the installer that would perform the upgrade, so its presence answers
        // exactly the question being asked - and unlike the ARP key it needs no
        // guess about which hive an all-users or per-user install landed in.
        return File.Exists(Path.Combine(directory, InnoUninstaller))
            ? new(InstallMedium.WindowsInstaller, directory, processPath)
            : new(InstallMedium.WindowsPortable, directory, processPath);
    }

    internal static InstallLocation DetectMac(string? processPath)
    {
        var directory = DirectoryOf(processPath);
        if (directory is null)
            return new(InstallMedium.Unknown, null);

        // build/macos/package.sh lays out Omnigit.app/Contents/MacOS/Omnigit. Walking
        // up to the bundle rather than keeping the executable's directory is what lets
        // an update replace the whole bundle, which is the only sound way to do it -
        // Info.plist, the natives and the signature all have to move together.
        var macOs = Path.GetFileName(directory);
        var contents = Path.GetDirectoryName(directory);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);

        if (macOs == "MacOS"
            && contents is not null && Path.GetFileName(contents) == "Contents"
            && bundle is not null && bundle.EndsWith(".app", StringComparison.Ordinal))
        {
            // The bundle is what gets replaced; `open` is what starts it, so that
            // launchd gives the new copy its own process rather than reusing ours.
            return new(InstallMedium.MacAppBundle, bundle, bundle);
        }

        return new(InstallMedium.Unknown, null);
    }

    /// <summary>Which package manager owns /usr on this system.</summary>
    /// <remarks>
    /// Asking the manager outright - <c>dpkg-query -S</c>, <c>rpm -qf</c> - would be
    /// exact, but this runs while the window is being built and a subprocess there is a
    /// stall on the one path the user is waiting for. The database's own directory is
    /// the next best thing and is never absent on a system managed by that tool.
    ///
    /// dpkg is tested first because a Debian system may carry rpm as a conversion tool
    /// while nothing on a Fedora system installs dpkg's database.
    /// </remarks>
    private static InstallMedium PackageManager()
    {
        if (Directory.Exists("/var/lib/dpkg"))
            return InstallMedium.DebPackage;

        if (Directory.Exists("/var/lib/rpm") || Directory.Exists("/usr/lib/sysimage/rpm"))
            return InstallMedium.RpmPackage;

        return InstallMedium.Unknown;
    }

    private static string? DirectoryOf(string? processPath) =>
        string.IsNullOrEmpty(processPath) ? null : Path.GetDirectoryName(Path.GetFullPath(processPath));
}
