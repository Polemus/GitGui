using System;
using System.IO;
using System.Linq;

namespace Omnigit.Services;

/// <summary>
/// Where the app keeps its own files, and the one-time carry-over from the name it
/// used to have.
/// </summary>
/// <remarks>
/// Renaming GitGui to Omnigit renamed this directory with it, which orphaned every
/// account, repository and host manifest the user had: the app started up looking
/// perfectly healthy and completely empty. The old directory is copied across on
/// first run rather than moved, so a build from before the rename still works.
/// </remarks>
public static class AppPaths
{
    private const string Name = "Omnigit";
    private const string FormerName = "GitGui";

    /// <summary>Marks the carry-over as done, so signing out doesn't resurrect the old state.</summary>
    private const string Marker = ".migrated-from-gitgui";

    /// <summary>
    /// %APPDATA%\Omnigit on Windows, ~/.config/Omnigit on Linux,
    /// ~/Library/Application Support/Omnigit on macOS.
    /// </summary>
    public static string Data { get; } = Resolve();

    public static string In(string name) => Path.Combine(Data, name);

    private static string Resolve()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(root, Name);

        try
        {
            Directory.CreateDirectory(directory);
            CarryOver(Path.Combine(root, FormerName), directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting with nothing beats not starting. The stores each fail soft too.
        }

        return directory;
    }

    internal static void CarryOver(string former, string current)
    {
        if (!Directory.Exists(former))
            return;

        // Only into a directory this app has never used: anything already here is
        // newer than what the old name holds.
        if (File.Exists(Path.Combine(current, Marker))
            || Directory.EnumerateFileSystemEntries(current).Any())
            return;

        CopyTree(former, current);
        File.WriteAllText(Path.Combine(current, Marker), string.Empty);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var copy = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, copy, overwrite: false);

            // Tokens in the plain-file fallback are 0600 and must stay that way.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(copy, File.GetUnixFileMode(file));
        }

        foreach (var child in Directory.EnumerateDirectories(source))
            CopyTree(child, Path.Combine(destination, Path.GetFileName(child)));
    }
}
