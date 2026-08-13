using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

// Avalonia 12 moved the clipboard to IDataTransfer; SetTextAsync survives as an extension here.
using Avalonia.Input.Platform;

namespace GitGui.Services;

/// <summary>
/// Lets a view model open a browser or reach the clipboard without referencing a window,
/// the same trick <see cref="IFolderPicker"/> uses for dialogs.
/// </summary>
public interface ISystemShell
{
    /// <summary>Opens a URL in the user's browser. False if there was nothing to open it with.</summary>
    Task<bool> OpenUrlAsync(Uri url);

    /// <summary>Puts text on the clipboard. False if the platform gave us no clipboard.</summary>
    Task<bool> CopyTextAsync(string text);
}

/// <summary>
/// Both operations hand off to another process - a browser, a clipboard manager - and
/// failing to reach one is an ordinary condition on a stripped-down desktop, not a fault.
/// So they report false and let the caller say something useful.
/// </summary>
public sealed class SystemShell : ISystemShell
{
    public async Task<bool> OpenUrlAsync(Uri url)
    {
        if (MainWindow is not { } window)
            return false;

        try
        {
            return await window.Launcher.LaunchUriAsync(url);
        }
        catch (Exception)
        {
            // Launching goes through xdg-open / ShellExecute / NSWorkspace, none of which
            // document what they throw when no handler is registered. The caller only
            // needs to know it didn't happen.
            return false;
        }
    }

    public async Task<bool> CopyTextAsync(string text)
    {
        if (MainWindow?.Clipboard is not { } clipboard)
            return false;

        try
        {
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception)
        {
            // On Linux the clipboard is a running process that owns the selection; if
            // nothing holds it, the set can fail outright.
            return false;
        }
    }

    private static Avalonia.Controls.Window? MainWindow
        => Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? window
            : null;
}
