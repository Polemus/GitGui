using System;
using System.IO;
using System.Threading;
using Avalonia.Threading;

namespace Omnigit.Services;

/// <summary>Tells the UI that something changed on disk under the current repository.</summary>
public interface IRepositoryWatcher : IDisposable
{
    /// <summary>Watches a new path, dropping whatever was being watched before.</summary>
    void Watch(string repositoryPath);

    /// <summary>Stops watching without disposing, e.g. when no repository is selected.</summary>
    void Stop();

    /// <summary>Raised on the UI thread, already debounced.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// One <see cref="FileSystemWatcher"/> over the whole working tree, including .git, so
/// both "someone edited a file" and "someone committed from a terminal" come through.
///
/// Two things make this practical. Everything is debounced, because a single git command
/// touches dozens of files and an editor save often writes twice. And the noisy paths are
/// filtered out: .git/objects churns on every operation, lock files appear and vanish in
/// pairs, and build output changes constantly while being invisible to git anyway.
/// </summary>
public sealed class RepositoryWatcher : IRepositoryWatcher
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(750);

    private readonly Timer _debounce;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public RepositoryWatcher()
        => _debounce = new Timer(_ => Raise(), null, Timeout.Infinite, Timeout.Infinite);

    public event EventHandler? Changed;

    public void Watch(string repositoryPath)
    {
        Stop();

        if (_disposed || !Directory.Exists(repositoryPath))
            return;

        var watcher = new FileSystemWatcher(repositoryPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                           | NotifyFilters.DirectoryName
                           | NotifyFilters.LastWrite
                           | NotifyFilters.Size,

            // A git operation can touch more files than the default 8KB buffer holds.
            InternalBufferSize = 64 * 1024,
        };

        watcher.Changed += OnFileSystemEvent;
        watcher.Created += OnFileSystemEvent;
        watcher.Deleted += OnFileSystemEvent;
        watcher.Renamed += OnFileSystemEvent;

        // Overflow means we missed events, so the safe response is to refresh anyway.
        watcher.Error += (_, _) => Schedule();

        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    public void Stop()
    {
        _debounce.Change(Timeout.Infinite, Timeout.Infinite);

        if (_watcher is not { } watcher)
            return;

        _watcher = null;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _debounce.Dispose();
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (IsNoise(e.FullPath))
            return;

        Schedule();
    }

    /// <summary>Restarts the quiet period, so a burst of events produces one refresh.</summary>
    private void Schedule() => _debounce.Change(Quiet, Timeout.InfiniteTimeSpan);

    private void Raise()
    {
        if (_disposed)
            return;

        // Watcher callbacks arrive on a thread-pool thread.
        Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private static bool IsNoise(string fullPath)
    {
        if (fullPath.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            return true;

        var segments = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        for (var i = 0; i < segments.Length; i++)
        {
            // Build output. Git ignores it, so refreshing on it only burns work - and
            // during a build it would fire continuously.
            if (segments[i] is "bin" or "obj")
                return true;

            // Inside .git, loose objects and reflogs churn on every operation without
            // changing anything we display. Matched against .git specifically so a
            // working-tree folder that happens to be called "objects" still counts.
            if (segments[i] is ".git" && i + 1 < segments.Length
                                      && segments[i + 1] is "objects" or "logs")
            {
                return true;
            }
        }

        return false;
    }
}
