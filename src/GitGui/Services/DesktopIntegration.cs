using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace GitGui.Services;

public enum DesktopIntegrationOutcome
{
    /// <summary>Not running from an AppImage, or not on Linux. Nothing to do.</summary>
    NotApplicable,

    /// <summary>The user asked us not to, with GITGUI_NO_DESKTOP_INTEGRATION.</summary>
    Declined,

    /// <summary>Already installed, and pointing at this same AppImage.</summary>
    AlreadyCurrent,

    /// <summary>Desktop entry and icons written under XDG_DATA_HOME.</summary>
    Installed,

    /// <summary>Something in the filesystem said no. The app still runs.</summary>
    Failed,
}

public readonly record struct DesktopIntegrationResult(
    DesktopIntegrationOutcome Outcome,
    string? Detail = null);

/// <summary>
/// Registers an AppImage with the desktop: a .desktop entry and the icon theme
/// files, under ~/.local/share.
/// </summary>
/// <remarks>
/// A .deb, .rpm or Flatpak installs those; an AppImage is one file and installs
/// nothing, so a desktop shell has no icon to show for it. GNOME and Plasma both
/// find the window's app by matching WM_CLASS against StartupWMClass in an
/// installed desktop entry, then take the icon from that entry's Icon= key. With
/// no entry to match, Ubuntu draws its generic executable icon - a cog - in the
/// dock, which is what this exists to stop.
///
/// The window's own icon (Window.Icon, which reaches X11 as _NET_WM_ICON) is not
/// a substitute: once a shell matches a window to a desktop entry it uses that
/// entry's icon and ignores the window's, so an entry whose Icon= names a file
/// nobody installed is worse than no entry at all.
///
/// Everything here is best effort. Failing to write to the home directory is an
/// ordinary condition - a read-only home, a hostile sandbox - so it reports what
/// happened rather than throwing.
/// </remarks>
public static class DesktopIntegration
{
    public const string AppId = "io.github.polemus.GitGui";

    /// <summary>Set this to any non-empty value to keep GitGui out of ~/.local/share.</summary>
    public const string OptOutVariable = "GITGUI_NO_DESKTOP_INTEGRATION";

    /// <summary>
    /// Stamped into the entry we write so the next launch can tell "already done"
    /// from "the AppImage moved, or was replaced by a newer one".
    /// </summary>
    private const string SourceKey = "X-GitGui-AppImage";

    public static Task<DesktopIntegrationResult> EnsureInstalledAsync() =>
        Task.Run(EnsureInstalled);

    public static DesktopIntegrationResult EnsureInstalled()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new(DesktopIntegrationOutcome.NotApplicable);

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OptOutVariable)))
            return new(DesktopIntegrationOutcome.Declined);

        // The AppImage runtime exports APPIMAGE (the .AppImage file the user
        // launched) and APPDIR (where it mounted itself this run). Neither exists
        // for the tarball, the .deb or the Flatpak, all of which are installed
        // properly already.
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrEmpty(appImage) || !File.Exists(appImage))
            return new(DesktopIntegrationOutcome.NotApplicable);

        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (string.IsNullOrEmpty(appDir) || !Directory.Exists(appDir))
            return new(DesktopIntegrationOutcome.NotApplicable);

        try
        {
            return Install(appDir, Path.GetFullPath(appImage));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(DesktopIntegrationOutcome.Failed, ex.Message);
        }
    }

    private static DesktopIntegrationResult Install(string appDir, string appImage)
    {
        var dataHome = DataHome();
        var source = Path.Combine(appDir, "usr", "share", "applications", $"{AppId}.desktop");
        if (!File.Exists(source))
            return new(DesktopIntegrationOutcome.Failed, $"no desktop entry at {source}");

        var target = Path.Combine(dataHome, "applications", $"{AppId}.desktop");
        var entry = Localise(File.ReadAllText(source), appImage);

        // The mount point in APPDIR changes on every launch, so the entry can only
        // be compared against itself - not against anything under appDir.
        if (File.Exists(target) && File.ReadAllText(target) == entry)
            return new(DesktopIntegrationOutcome.AlreadyCurrent);

        var copied = CopyIcons(appDir, dataHome);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, entry);

        // Shells watch these directories, so nothing needs to be told. The cache is
        // only a speed-up and is rebuilt from the files either way.
        return new(
            DesktopIntegrationOutcome.Installed,
            $"{target} and {copied} icon{(copied == 1 ? "" : "s")}");
    }

    /// <summary>
    /// Copies the hicolor icons out of the AppDir into the user's icon theme, which
    /// is what makes the entry's <c>Icon=io.github.polemus.GitGui</c> resolve to a
    /// picture instead of falling back to the cog.
    /// </summary>
    private static int CopyIcons(string appDir, string dataHome)
    {
        var from = Path.Combine(appDir, "usr", "share", "icons");
        if (!Directory.Exists(from))
            return 0;

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(from, $"{AppId}.*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dataHome, "icons", Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
            copied++;
        }

        return copied;
    }

    private static string DataHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
    }

    /// <summary>
    /// Rewrites the packaged entry so it points at this AppImage. Exec=gitgui is
    /// right for the packages, where /usr/bin/gitgui exists; here the only thing
    /// that can be run is the .AppImage file itself, wherever the user left it.
    /// </summary>
    /// <remarks>
    /// Internal so the rewrite can be tested without a mounted AppImage.
    /// </remarks>
    internal static string Localise(string desktopEntry, string appImage)
    {
        var quoted = QuoteExec(appImage);
        var lines = new List<string>
        {
            $"# Written by GitGui on first run from an AppImage. Set {OptOutVariable}",
            "# to stop that, and delete this file.",
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in desktopEntry.ReplaceLineEndings("\n").Split('\n'))
        {
            // The packaged file's comments explain the packaging, and go stale the
            // moment Exec is rewritten. This copy carries its own header instead.
            if (line.TrimStart().StartsWith('#'))
                continue;

            var key = KeyOf(line);
            switch (key)
            {
                case "Exec":
                    lines.Add($"Exec={quoted}");
                    break;

                // TryExec makes the shell hide the entry when the AppImage is
                // deleted or moved, rather than offering a launcher that fails.
                case "TryExec":
                    lines.Add($"TryExec={quoted}");
                    break;

                case SourceKey:
                    lines.Add($"{SourceKey}={appImage}");
                    break;

                default:
                    lines.Add(line);
                    break;
            }

            if (key is not null)
                seen.Add(key);
        }

        // Trailing blank lines from the source file would push appended keys out of
        // the [Desktop Entry] group, so append before them.
        var end = lines.Count;
        while (end > 0 && string.IsNullOrWhiteSpace(lines[end - 1]))
            end--;

        var appended = new List<string>();
        if (!seen.Contains("Exec")) appended.Add($"Exec={quoted}");
        if (!seen.Contains("TryExec")) appended.Add($"TryExec={quoted}");
        if (!seen.Contains(SourceKey)) appended.Add($"{SourceKey}={appImage}");

        lines.InsertRange(end, appended);

        return string.Join("\n", lines).TrimEnd('\n') + "\n";
    }

    /// <summary>The key of a <c>Key=value</c> line, or null for comments, blanks and group headers.</summary>
    private static string? KeyOf(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is '#' or '[')
            return null;

        var equals = line.IndexOf('=');
        return equals <= 0 ? null : line[..equals].Trim();
    }

    /// <summary>
    /// Quotes a path for the Exec key. The desktop entry spec gives the value its
    /// own quoting rules - reserved characters have to be inside double quotes, and
    /// a backslash escapes only inside them - so a path with a space or a dollar in
    /// it needs this rather than shell quoting.
    /// </summary>
    internal static string QuoteExec(string path)
    {
        if (!path.Any(c => c is ' ' or '\t' or '"' or '\'' or '\\' or '>' or '<' or '~'
                or '|' or '&' or ';' or '$' or '*' or '?' or '#' or '(' or ')' or '`'))
        {
            return path;
        }

        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '"' or '\\' or '$' or '`')
                quoted.Append('\\');
            quoted.Append(c);
        }

        return quoted.Append('"').ToString();
    }
}
