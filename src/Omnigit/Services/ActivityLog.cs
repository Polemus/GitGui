using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace Omnigit.Services;

public enum ActivityLevel
{
    Trace,
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>One line in the activity console.</summary>
public sealed class ActivityEntry
{
    public required ActivityLevel Level { get; init; }
    public required string Message { get; init; }

    /// <summary>Extra context (a stack trace, a server response) shown indented.</summary>
    public string? Detail { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    public string Timestamp => At.ToString("HH:mm:ss");
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    // Styling hooks, bound to Classes.* in the view.
    public bool IsTrace => Level == ActivityLevel.Trace;
    public bool IsSuccess => Level == ActivityLevel.Success;
    public bool IsWarning => Level == ActivityLevel.Warning;
    public bool IsError => Level == ActivityLevel.Error;
}

/// <summary>
/// Collects what the app is doing so the user can see it, rather than operations
/// failing silently or the app breaking.
/// </summary>
public interface IActivityLog
{
    ReadOnlyObservableCollection<ActivityEntry> Entries { get; }

    /// <summary>Raised when an error is logged, so the UI can reveal the console.</summary>
    event EventHandler? ErrorLogged;

    void Write(ActivityLevel level, string message, string? detail = null);
    void Clear();
}

/// <summary>
/// In-memory log, capped so a chatty fetch can't grow without bound.
/// </summary>
/// <remarks>
/// Writes are marshalled to the UI thread. Git work runs on pooled threads and
/// libgit2's progress callbacks fire on whichever thread is doing the transfer, so
/// appending directly would mutate a bound collection off-thread.
/// </remarks>
public sealed class ActivityLog : IActivityLog
{
    private const int MaxEntries = 500;

    private readonly ObservableCollection<ActivityEntry> _entries = [];

    public ActivityLog() => Entries = new ReadOnlyObservableCollection<ActivityEntry>(_entries);

    public ReadOnlyObservableCollection<ActivityEntry> Entries { get; }

    public event EventHandler? ErrorLogged;

    public void Write(ActivityLevel level, string message, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var entry = new ActivityEntry { Level = level, Message = message, Detail = detail };

        if (Dispatcher.UIThread.CheckAccess())
            Append(entry);
        else
            Dispatcher.UIThread.Post(() => Append(entry));
    }

    public void Clear()
    {
        if (Dispatcher.UIThread.CheckAccess())
            _entries.Clear();
        else
            Dispatcher.UIThread.Post(_entries.Clear);
    }

    private void Append(ActivityEntry entry)
    {
        _entries.Add(entry);

        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);

        if (entry.Level == ActivityLevel.Error)
            ErrorLogged?.Invoke(this, EventArgs.Empty);
    }
}
