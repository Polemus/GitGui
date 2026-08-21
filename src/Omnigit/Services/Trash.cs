using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Omnigit.Services;

public enum TrashOutcome
{
    /// <summary>It is in the desktop's trash, and can be put back from there.</summary>
    Trashed,

    /// <summary>There was nothing at that path to begin with.</summary>
    NotFound,

    /// <summary>Nothing was deleted. <see cref="TrashResult.Detail"/> says why.</summary>
    Failed,
}

public readonly record struct TrashResult(TrashOutcome Outcome, string? Detail = null);

/// <summary>
/// Moves a directory to the desktop's trash.
/// </summary>
/// <remarks>
/// Trash rather than <see cref="Directory.Delete(string, bool)"/>, and this is the whole
/// point of the class. A clone is not only a copy of what is on the server: it holds
/// uncommitted edits, untracked files, ignored ones like a .env, and stashes - none of
/// which exist anywhere else. Deleting that outright on the strength of one menu click
/// is not a risk worth taking when every desktop already has a place to put things you
/// are only fairly sure about.
///
/// **A failure never falls back to deleting anyway.** Doing the more destructive thing
/// because the safer one was unavailable is precisely backwards, and the user would have
/// no way to know which had happened. It reports what stopped it and leaves the files.
/// </remarks>
public static class Trash
{
    public static Task<TrashResult> MoveDirectoryAsync(string path) =>
        Task.Run(() => MoveDirectory(path));

    public static TrashResult MoveDirectory(string path)
    {
        if (!Directory.Exists(path))
            return new(TrashOutcome.NotFound);

        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsRecycle(path);

            if (OperatingSystem.IsMacOS())
                return MacFinderDelete(path);

            return LinuxTrash(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            return new(TrashOutcome.Failed, ex.Message);
        }
    }

    // ---- Windows -----------------------------------------------------------

    private const int FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public int wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>The Recycle Bin, through the same call Explorer's own Delete makes.</summary>
    /// <remarks>
    /// <c>pFrom</c> is a list, not a string: it must end in *two* nulls, and a single
    /// terminator is read as an unterminated list. The marshaller adds one, so the other
    /// is appended by hand - miss it and the call reads past the path into whatever
    /// follows it in memory.
    /// </remarks>
    private static TrashResult WindowsRecycle(string path)
    {
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };

        var code = SHFileOperation(ref operation);

        if (code != 0)
            return new(TrashOutcome.Failed, $"The Recycle Bin refused it (code {code}).");

        if (operation.fAnyOperationsAborted)
            return new(TrashOutcome.Failed, "The delete was cancelled.");

        return new(TrashOutcome.Trashed);
    }

    // ---- macOS -------------------------------------------------------------

    /// <summary>
    /// Asks the Finder, which is the only route to the real Trash without linking
    /// against AppKit for <c>NSFileManager.trashItemAtURL</c>.
    /// </summary>
    /// <remarks>Untested - there is no Mac. Same gap as the update path's .dmg.</remarks>
    private static TrashResult MacFinderDelete(string path)
    {
        // POSIX file, not a HFS path: the latter is colon-separated and a directory name
        // containing a slash would be mangled into a different location entirely.
        var script = $"tell application \"Finder\" to delete POSIX file \"{path.Replace("\"", "\\\"")}\"";

        var run = Run("/usr/bin/osascript", ["-e", script]);

        return run.ExitCode == 0
            ? new(TrashOutcome.Trashed)
            : new(TrashOutcome.Failed, Firstline(run.Error) ?? "The Finder refused it.");
    }

    // ---- Linux -------------------------------------------------------------

    /// <summary>
    /// <c>gio trash</c> where it exists, and the freedesktop specification by hand where
    /// it does not.
    /// </summary>
    /// <remarks>
    /// gio is preferred because it handles what the fallback does not: a file on another
    /// filesystem belongs in *that* volume's <c>.Trash-$uid</c>, not the home one, and
    /// moving it to the home trash would be a copy across devices rather than a rename.
    /// The fallback therefore only claims the home trash, and refuses rather than
    /// guesses when the directory is not on the same filesystem.
    /// </remarks>
    private static TrashResult LinuxTrash(string path)
    {
        if (File.Exists("/usr/bin/gio"))
        {
            var run = Run("/usr/bin/gio", ["trash", "--", path]);
            if (run.ExitCode == 0)
                return new(TrashOutcome.Trashed);
        }

        return XdgTrash(path);
    }

    private static TrashResult XdgTrash(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        var root = !string.IsNullOrEmpty(dataHome) && Path.IsPathRooted(dataHome)
            ? Path.Combine(dataHome, "Trash")
            : Path.Combine(home, ".local", "share", "Trash");

        var files = Path.Combine(root, "files");
        var info = Path.Combine(root, "info");

        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        var name = UniqueName(files, info, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));

        // The .trashinfo is written first: a file in files/ with no info beside it is an
        // orphan the desktop cannot restore or even name, whereas an info file with
        // nothing to describe is ignored.
        File.WriteAllText(
            Path.Combine(info, name + ".trashinfo"),
            "[Trash Info]\n"
            + $"Path={Uri.EscapeDataString(path).Replace("%2F", "/")}\n"
            + $"DeletionDate={DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n");

        try
        {
            Directory.Move(path, Path.Combine(files, name));
        }
        catch (IOException ex)
        {
            File.Delete(Path.Combine(info, name + ".trashinfo"));

            // Almost always a different filesystem, which the specification says belongs
            // in that volume's own trash rather than here.
            return new(
                TrashOutcome.Failed,
                $"Could not move it to the trash ({ex.Message}). Install gio, or delete it yourself.");
        }

        return new(TrashOutcome.Trashed);
    }

    /// <summary>A name taken by neither files/ nor info/, so a restore cannot collide.</summary>
    private static string UniqueName(string files, string info, string wanted)
    {
        var name = wanted;

        for (var n = 2; File.Exists(Path.Combine(info, name + ".trashinfo"))
                        || Directory.Exists(Path.Combine(files, name))
                        || File.Exists(Path.Combine(files, name)); n++)
        {
            name = $"{wanted}.{n}";
        }

        return name;
    }

    // ---- Running a helper --------------------------------------------------

    private static (int ExitCode, string Error) Run(string program, string[] arguments)
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
            return (-1, $"could not start {program}");

        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, error);
    }

    private static string? Firstline(string text)
    {
        var line = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return line.Length == 0 ? null : line[0].Trim();
    }
}
