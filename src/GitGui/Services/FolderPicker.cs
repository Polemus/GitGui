using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace GitGui.Services;

/// <summary>Lets a view model ask for a directory without referencing a window.</summary>
public interface IFolderPicker
{
    Task<string?> PickAsync(string title);
}

/// <summary>
/// Folder picker over Avalonia's StorageProvider, which maps to the native dialog
/// on each platform (XDG portal, IFileDialog, NSOpenPanel).
/// </summary>
public sealed class FolderPicker : IFolderPicker
{
    public async Task<string?> PickAsync(string title)
    {
        if (Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return null;
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
