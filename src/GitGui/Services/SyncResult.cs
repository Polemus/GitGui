namespace GitGui.Services;

/// <summary>How a fetch, pull or push ended.</summary>
public enum SyncOutcome
{
    Succeeded,

    /// <summary>The server wanted credentials and no account was signed in.</summary>
    NotSignedIn,

    /// <summary>An account was signed in but the server rejected its token.</summary>
    CredentialsRejected,

    /// <summary>The repository has no remote configured.</summary>
    NoRemote,

    /// <summary>Anything else — network down, rejected push, merge conflict.</summary>
    Failed,
}

/// <summary>
/// The result of a network git operation.
/// </summary>
/// <remarks>
/// Returned rather than thrown. Being signed out is an ordinary thing for a git client
/// to encounter, not an exceptional one, and modelling it as an exception both muddles
/// the control flow and makes the debugger break on every occurrence during development.
/// Genuine faults still surface as exceptions.
/// </remarks>
public sealed record SyncResult(SyncOutcome Outcome, string Message)
{
    public bool Succeeded => Outcome == SyncOutcome.Succeeded;

    public static SyncResult Ok(string message) => new(SyncOutcome.Succeeded, message);
}
