using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls; // GridLength, for the resizable pane widths below.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Omnigit.HostProviders;
using Omnigit.Models;
using Omnigit.Services;

namespace Omnigit.ViewModels;

/// <summary>
/// Drives the shell against real repositories. Every git call is pushed onto a
/// background thread; the awaits resume on the UI thread, so collection updates
/// below each await are already marshalled correctly.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private const int HistoryLimit = 100;

    private readonly IGitService _git;
    private readonly IRepositoryStore _store;
    private readonly IFolderPicker _picker;
    private readonly HostProviderRegistry _hosts;
    private readonly IAccountStore _accountStore;
    private readonly ICredentialStore _credentials;
    private readonly IActivityLog _log;
    private readonly ISystemShell _shell;
    private readonly IRepositoryWatcher _watcher;
    private readonly bool _isDesignTime;

    /// <summary>Set by an automatic refresh so the commit's file list can restore itself.</summary>
    private string? _restoreCommitFilePath;

    /// <summary>Design-time constructor. Fills the previewer from sample data only.</summary>
    public MainWindowViewModel()
        : this(new GitService(), new RepositoryStore(), new FolderPicker(),
               HostProviderRegistry.Create(new System.Net.Http.HttpClient()),
               new AccountStore(new FileCredentialStore()), new FileCredentialStore(),
               new ActivityLog(), new SystemShell(), new RepositoryWatcher(), designTime: true)
    {
        LoadDesignTimeData();
    }

    public MainWindowViewModel(
        IGitService git,
        IRepositoryStore store,
        IFolderPicker picker,
        HostProviderRegistry hosts,
        IAccountStore accountStore,
        ICredentialStore credentials,
        IActivityLog log,
        ISystemShell shell,
        IRepositoryWatcher watcher)
        : this(git, store, picker, hosts, accountStore, credentials, log, shell, watcher,
               designTime: false)
    {
    }

    private MainWindowViewModel(
        IGitService git,
        IRepositoryStore store,
        IFolderPicker picker,
        HostProviderRegistry hosts,
        IAccountStore accountStore,
        ICredentialStore credentials,
        IActivityLog log,
        ISystemShell shell,
        IRepositoryWatcher watcher,
        bool designTime)
    {
        _git = git;
        _store = store;
        _picker = picker;
        _hosts = hosts;
        _accountStore = accountStore;
        _credentials = credentials;
        _log = log;
        _shell = shell;
        _watcher = watcher;
        _isDesignTime = designTime;

        if (!designTime)
            watcher.Changed += OnRepositoryChangedOnDisk;

        // An error the user can't see is an error they can't act on.
        log.ErrorLogged += (_, _) => IsConsoleExpanded = true;

        foreach (var provider in hosts.Providers)
            Providers.Add(provider);

        SelectedProvider = Providers.FirstOrDefault();
    }

    public ObservableCollection<RepositoryInfo> Repositories { get; } = [];
    public ObservableCollection<HostGroupViewModel> RepositoryGroups { get; } = [];
    public ObservableCollection<BranchInfo> Branches { get; } = [];
    public ObservableCollection<CommitInfo> History { get; } = [];
    public ObservableCollection<FileChangeViewModel> Changes { get; } = [];
    public ObservableCollection<FileChange> SelectedCommitFiles { get; } = [];

    public ObservableCollection<HostAccount> Accounts { get; } = [];
    public ObservableCollection<IHostProvider> Providers { get; } = [];
    public ObservableCollection<GitHost> Hosts { get; } = [];

    // ---- Sign-in -----------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseBrowserLogin))]
    [NotifyPropertyChangedFor(nameof(TokenHelpText))]
    public partial IHostProvider? SelectedProvider { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseBrowserLogin))]
    public partial string SignInServerUrl { get; set; } = "https://github.com";

    [ObservableProperty]
    public partial string SignInToken { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DeviceLogin? PendingDeviceLogin { get; set; }

    public bool HasPendingDeviceLogin => PendingDeviceLogin is not null;

    /// <summary>
    /// Depends on the server as well as the provider: Omnigit's built-in client id is
    /// registered on github.com, so the button hides again if the URL points elsewhere.
    /// </summary>
    public bool CanUseBrowserLogin
        => SelectedProvider is GitHubProvider github
           && Uri.TryCreate(SignInServerUrl, UriKind.Absolute, out var baseUrl)
           && github.CanUseBrowserLogin(baseUrl);

    public string TokenHelpText => SelectedProvider is null
        ? string.Empty
        : $"Create a token on {SelectedProvider.DisplayName} and paste it here. "
          + "It is stored in " + _credentials.Description + ".";

    public string CredentialBackendLabel => $"Tokens are stored in {_credentials.Description}.";

    public bool CredentialBackendIsWeak => !_credentials.IsSecure;

    public bool HasAccounts => Accounts.Count > 0;

    /// <summary>Manifest problems worth telling the user about.</summary>
    public string? HostWarnings => _hosts.Warnings.Count == 0
        ? null
        : string.Join("  ", _hosts.Warnings);

    public bool HasHostWarnings => HostWarnings is not null;

    // ---- Activity console --------------------------------------------------

    public ReadOnlyObservableCollection<ActivityEntry> LogEntries => _log.Entries;

    [ObservableProperty]
    public partial bool IsConsoleExpanded { get; set; }

    /// <summary>
    /// Height of the console's row. Auto while collapsed, so the header alone sets it;
    /// an absolute height once open, which is what makes the splitter able to drag it.
    /// </summary>
    [ObservableProperty]
    public partial GridLength ConsoleHeight { get; set; } = GridLength.Auto;

    /// <summary>Remembers how tall the user dragged it, so reopening returns there.</summary>
    private GridLength _lastConsoleHeight = new(260);

    /// <summary>
    /// Hooked rather than done in the toggle command, because an error opens the console
    /// on its own and would otherwise leave the row still sized for a collapsed one.
    /// </summary>
    partial void OnIsConsoleExpandedChanged(bool value)
    {
        if (value)
        {
            ConsoleHeight = _lastConsoleHeight;
            return;
        }

        if (ConsoleHeight.IsAbsolute && ConsoleHeight.Value > 0)
            _lastConsoleHeight = ConsoleHeight;

        ConsoleHeight = GridLength.Auto;
    }

    /// <summary>Most recent line, shown on the collapsed bar.</summary>
    public ActivityEntry? LatestEntry => _log.Entries.Count > 0 ? _log.Entries[^1] : null;

    public bool HasLogEntries => _log.Entries.Count > 0;

    [RelayCommand]
    private void ToggleConsole() => IsConsoleExpanded = !IsConsoleExpanded;

    /// <summary>
    /// Nothing in the UI calls this since the console header lost its Clear button, but
    /// <see cref="ActivityLog"/> is the only thing holding those entries, so the way to
    /// empty it stays here rather than being rebuilt from scratch later.
    /// </summary>
    [RelayCommand]
    private void ClearLog()
    {
        _log.Clear();
        OnPropertyChanged(nameof(LatestEntry));
        OnPropertyChanged(nameof(HasLogEntries));
    }

    private void Log(ActivityLevel level, string message, string? detail = null)
    {
        _log.Write(level, message, detail);
        OnPropertyChanged(nameof(LatestEntry));
        OnPropertyChanged(nameof(HasLogEntries));
    }

    // ---- Loading / errors --------------------------------------------------

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasRepositories => Repositories.Count > 0;

    // ---- Selection ---------------------------------------------------------

    [ObservableProperty]
    public partial RepositoryInfo? SelectedRepository { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonLabel))]
    [NotifyPropertyChangedFor(nameof(HeadLabel))]
    public partial BranchInfo? SelectedBranch { get; set; }

    [ObservableProperty]
    public partial FileChangeViewModel? SelectedChange { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAmendSelectedCommit))]
    [NotifyCanExecuteChangedFor(nameof(AmendSelectedCommitCommand))]
    public partial CommitInfo? SelectedCommit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChangesTab))]
    [NotifyPropertyChangedFor(nameof(IsHistoryTab))]
    public partial int SelectedTabIndex { get; set; }

    public bool IsChangesTab => SelectedTabIndex == 0;
    public bool IsHistoryTab => SelectedTabIndex == 1;

    // ---- Browse and clone --------------------------------------------------

    [ObservableProperty]
    public partial bool IsClonePageVisible { get; set; }

    /// <summary>What the list is filtered down to. Rebuilt rather than filtered in the view.</summary>
    public ObservableCollection<RemoteRepositoryViewModel> RemoteRepositories { get; } = [];

    /// <summary>Everything fetched, before the filter is applied.</summary>
    private readonly List<RemoteRepositoryViewModel> _allRemotes = [];

    [ObservableProperty]
    public partial bool IsLoadingRemotes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoteResults))]
    public partial string RemoteFilter { get; set; } = string.Empty;

    partial void OnRemoteFilterChanged(string value) => ApplyRemoteFilter();

    public bool HasRemoteResults => RemoteRepositories.Count > 0;

    public string RemoteEmptyLabel => _allRemotes.Count == 0
        ? "Sign in to a hosting site to browse what you can clone."
        : "Nothing matches that filter.";

    // ---- Settings ----------------------------------------------------------

    [ObservableProperty]
    public partial bool IsSettingsPageVisible { get; set; }

    /// <summary>
    /// Which section of settings is showing. An int rather than an enum so the tab rail
    /// can pass one through CommandParameter without a converter; there will be more.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccountsSection))]
    [NotifyPropertyChangedFor(nameof(IsHostsSection))]
    public partial int SettingsSection { get; set; }

    public bool IsAccountsSection => SettingsSection == 0;
    public bool IsHostsSection => SettingsSection == 1;

    /// <summary>Every site Omnigit knows about, whatever the description came from.</summary>
    public ObservableCollection<HostEntryViewModel> HostEntries { get; } = [];

    /// <summary>Non-null while the add/edit host form is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingHost))]
    public partial HostDraftViewModel? HostDraft { get; set; }

    public bool IsEditingHost => HostDraft is not null;

    // ---- Live repository status -------------------------------------------
    // Held here rather than on RepositoryInfo, which stays immutable identity.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncActionLabel))]
    [NotifyPropertyChangedFor(nameof(SyncCountLabel))]
    [NotifyPropertyChangedFor(nameof(HasSyncCount))]
    [NotifyPropertyChangedFor(nameof(CanAmend))]
    [NotifyPropertyChangedFor(nameof(CanAmendSelectedCommit))]
    [NotifyCanExecuteChangedFor(nameof(AmendSelectedCommitCommand))]
    public partial int Ahead { get; set; }

    /// <summary>False when the branch is not on the remote yet; see <see cref="CanAmend"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncActionLabel))]
    [NotifyPropertyChangedFor(nameof(CanAmend))]
    [NotifyPropertyChangedFor(nameof(CanAmendSelectedCommit))]
    [NotifyCanExecuteChangedFor(nameof(AmendSelectedCommitCommand))]
    public partial bool IsPublished { get; set; }

    /// <summary>False for a repository with no remote, where publishing means nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncActionLabel))]
    public partial bool HasRemote { get; set; }

    /// <summary>The branch exists nowhere but here, and there is a remote to send it to.</summary>
    public bool CanPublish => HasRemote && !IsPublished;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncActionLabel))]
    [NotifyPropertyChangedFor(nameof(SyncCountLabel))]
    [NotifyPropertyChangedFor(nameof(HasSyncCount))]
    public partial int Behind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncDetailLabel))]
    public partial DateTimeOffset? LastFetched { get; set; }

    /// <summary>
    /// True while HEAD points at a commit rather than a branch, which is where opening
    /// an older commit leaves you. Anything committed here belongs to no branch.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadLabel))]
    [NotifyPropertyChangedFor(nameof(HeadDetailLabel))]
    [NotifyPropertyChangedFor(nameof(CommitButtonLabel))]
    [NotifyPropertyChangedFor(nameof(CanAmend))]
    [NotifyPropertyChangedFor(nameof(CanAmendSelectedCommit))]
    [NotifyCanExecuteChangedFor(nameof(AmendSelectedCommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertSelectedCommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetToSelectedCommitCommand))]
    public partial bool IsDetachedHead { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadLabel))]
    [NotifyPropertyChangedFor(nameof(HeadDetailLabel))]
    public partial string HeadShortSha { get; set; } = string.Empty;

    /// <summary>What the toolbar shows where the branch name goes.</summary>
    public string HeadLabel => IsDetachedHead ? HeadShortSha : SelectedBranch?.Name ?? "—";

    public string HeadDetailLabel => IsDetachedHead ? "Not on a branch" : "Current branch";

    // ---- An operation git could not finish on its own ----------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingOperation))]
    [NotifyPropertyChangedFor(nameof(PendingOperationLabel))]
    [NotifyPropertyChangedFor(nameof(CommitButtonLabel))]
    public partial RepositoryOperation PendingOperation { get; set; }

    /// <summary>Paths git left for the user to decide about, newest listing first.</summary>
    public ObservableCollection<string> ConflictedPaths { get; } = [];

    public bool HasPendingOperation => PendingOperation != RepositoryOperation.None;

    public bool HasConflicts => ConflictedPaths.Count > 0;

    public string PendingOperationName => PendingOperation switch
    {
        RepositoryOperation.Merge => "merge",
        RepositoryOperation.Revert => "revert",
        RepositoryOperation.CherryPick => "cherry-pick",
        RepositoryOperation.Rebase => "rebase",
        _ => "operation",
    };

    public string PendingOperationLabel => ConflictedPaths.Count switch
    {
        0 => $"The {PendingOperationName} went through — commit it to finish.",
        1 => $"This {PendingOperationName} stopped on 1 file git could not merge on its own.",
        var n => $"This {PendingOperationName} stopped on {n} files git could not merge on its own.",
    };

    public string ConflictHelpLabel =>
        "Keep one side whole, or edit the file yourself and mark it resolved. "
        + "Committing finishes the operation; abandoning puts everything back.";

    /// <remarks>
    /// Publish comes first because a branch the remote has never seen has nothing to be
    /// ahead or behind of, and "Fetch origin" was the wrong offer there - the whole point
    /// is to get the branch out.
    /// </remarks>
    public string SyncActionLabel => CanPublish ? "Publish branch"
                                   : Behind > 0 ? "Pull origin"
                                   : Ahead > 0 ? "Push origin"
                                   : "Fetch origin";

    public string SyncDetailLabel => SelectedRepository is null ? string.Empty
        : LastFetched is { } when ? $"Last fetched {TimeFormat.Relative(when)}"
        : "Never fetched";

    public string SyncCountLabel => Behind > 0 ? $"↓ {Behind}"
                                  : Ahead > 0 ? $"↑ {Ahead}"
                                  : string.Empty;

    public bool HasSyncCount => !string.IsNullOrEmpty(SyncCountLabel);

    // ---- Commit box --------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial string CommitSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CommitDescription { get; set; } = string.Empty;

    public int StagedCount => Changes.Count(c => c.IsStaged);

    public string StagedCountLabel => Changes.Count switch
    {
        0 => "No local changes",
        1 => "1 changed file",
        _ => $"{Changes.Count} changed files",
    };

    // Amending only needs a message; re-wording the last commit without touching any
    // file is a perfectly ordinary thing to want. Conflicts are the one hard block:
    // committing markers is the mistake this whole panel exists to prevent.
    public bool CanCommit => (StagedCount > 0 || IsAmending)
                             && !string.IsNullOrWhiteSpace(CommitSummary)
                             && !HasConflicts
                             && !IsBusy;

    /// <summary>
    /// Amending rewrites history, so it is offered only while the last commit is still
    /// local. Once pushed, changing it would need a force-push, which is not something
    /// to make available from a menu.
    /// </summary>
    public bool CanAmend => SelectedRepository is not null
                            && !IsDetachedHead
                            && (Ahead > 0 || !IsPublished);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    [NotifyPropertyChangedFor(nameof(CommitButtonLabel))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial bool IsAmending { get; set; }

    /// <summary>Loads or clears the previous message as amend mode goes on and off.</summary>
    partial void OnIsAmendingChanged(bool value)
    {
        if (SelectedRepository is not { } repo)
            return;

        if (!value)
        {
            CommitSummary = string.Empty;
            CommitDescription = string.Empty;
            return;
        }

        if (_git.GetLastCommitMessage(repo.LocalPath) is not { } message)
            return;

        CommitSummary = message.Summary;
        CommitDescription = message.Description;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateBranchCommand))]
    public partial string NewBranchName { get; set; } = string.Empty;

    // ---- Stashes -----------------------------------------------------------

    /// <summary>Stashes belonging to the branch that's checked out. Others stay hidden.</summary>
    public ObservableCollection<StashInfo> BranchStashes { get; } = [];

    public bool HasBranchStashes => BranchStashes.Count > 0;

    public string StashLabel => BranchStashes.Count == 1
        ? "You have stashed changes on this branch"
        : $"You have {BranchStashes.Count} sets of stashed changes on this branch";

    /// <summary>The prompt shown when switching branches would abandon uncommitted work.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingBranchSwitch))]
    public partial BranchSwitchViewModel? PendingBranchSwitch { get; set; }

    public bool HasPendingBranchSwitch => PendingBranchSwitch is not null;

    // ---- Questions asked about one commit ----------------------------------
    // One property per prompt rather than one shared slot: they ask different things,
    // and a shared slot would need a type test in every binding.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBranchFromCommit))]
    public partial BranchFromCommitViewModel? BranchFromCommit { get; set; }

    public bool HasBranchFromCommit => BranchFromCommit is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTagDraft))]
    public partial TagDraftViewModel? TagDraft { get; set; }

    public bool HasTagDraft => TagDraft is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCherryPickDraft))]
    public partial CherryPickDraftViewModel? CherryPickDraft { get; set; }

    public bool HasCherryPickDraft => CherryPickDraft is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResetDraft))]
    public partial ResetDraftViewModel? ResetDraft { get; set; }

    public bool HasResetDraft => ResetDraft is not null;

    /// <summary>
    /// Set while abandoning an operation waits on confirmation. Abandoning resets hard,
    /// so whatever was resolved by hand goes with it — the same kind of unrecoverable
    /// step as discarding a file, and worth the same extra click.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AbortSummary))]
    public partial bool IsConfirmingAbort { get; set; }

    public string AbortSummary =>
        $"Everything this {PendingOperationName} changed, including any conflicts you have already "
        + "sorted out, goes back to the last commit. This cannot be undone.";

    // ---- Changed-file context menu -----------------------------------------

    /// <summary>
    /// The file a discard is waiting on confirmation for. Discarding cannot be undone -
    /// the change was never committed, so there is nothing to recover it from - which is
    /// the one thing in this app worth an extra click.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingDiscard))]
    [NotifyPropertyChangedFor(nameof(PendingDiscardSummary))]
    public partial FileChangeViewModel? PendingDiscard { get; set; }

    public bool HasPendingDiscard => PendingDiscard is not null;

    public string PendingDiscardSummary => PendingDiscard is not { } change
        ? string.Empty
        : $"{change.Path} goes back to its last committed state. This cannot be undone.";

    [RelayCommand]
    private void AskDiscardChanges(FileChangeViewModel? change)
    {
        if (change is not null)
            PendingDiscard = change;
    }

    [RelayCommand]
    private void CancelDiscard() => PendingDiscard = null;

    [RelayCommand]
    private async Task ConfirmDiscardAsync()
    {
        if (PendingDiscard is not { } change || SelectedRepository is not { } repo)
            return;

        var path = repo.LocalPath;
        var target = change.Path;
        PendingDiscard = null;

        await RunAsync(async () =>
        {
            await Task.Run(() => _git.DiscardChanges(path, [target]));
            Log(ActivityLevel.Success, $"Discarded changes to {target}");
        });

        await OpenRepositoryAsync(repo);
    }

    [RelayCommand]
    private async Task IgnoreAsync(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || SelectedRepository is not { } repo)
            return;

        var path = repo.LocalPath;

        await RunAsync(async () =>
        {
            await Task.Run(() => _git.AddToGitignore(path, pattern));
            Log(ActivityLevel.Success, $"Added {pattern} to .gitignore");
        });

        await OpenRepositoryAsync(repo);
    }

    [RelayCommand]
    private async Task CopyFilePathAsync(FileChangeViewModel? change)
    {
        if (change is null || SelectedRepository is not { } repo)
            return;

        var full = System.IO.Path.Combine(
            await Task.Run(() => _git.GetWorkingDirectory(repo.LocalPath)),
            change.Path);

        await CopyAsync(full, "file path");
    }

    [RelayCommand]
    private async Task CopyRelativeFilePathAsync(FileChangeViewModel? change)
    {
        if (change is not null)
            await CopyAsync(change.Path, "relative file path");
    }

    [RelayCommand]
    private async Task ShowInFileManagerAsync(FileChangeViewModel? change)
    {
        if (change is null || SelectedRepository is not { } repo)
            return;

        var full = System.IO.Path.Combine(
            await Task.Run(() => _git.GetWorkingDirectory(repo.LocalPath)),
            change.Path);

        if (!await _shell.ShowInFileManagerAsync(full))
            Log(ActivityLevel.Warning, "Could not open a file manager");
    }

    private async Task CopyAsync(string text, string what)
    {
        if (await _shell.CopyTextAsync(text))
            Log(ActivityLevel.Info, $"Copied the {what} to the clipboard");
        else
            Log(ActivityLevel.Warning, "Could not reach the clipboard");
    }

    public bool CanCreateBranch => !string.IsNullOrWhiteSpace(NewBranchName)
                                   && SelectedRepository is not null
                                   && !IsBusy;

    public string CommitButtonLabel => IsAmending ? "Amend last commit"
        : HasPendingOperation ? $"Finish the {PendingOperationName}"
        : IsDetachedHead ? $"Commit onto {HeadShortSha}"
        : $"Commit to {SelectedBranch?.Name ?? "branch"}";

    public string CommitSummaryPlaceholder
    {
        get
        {
            var staged = Changes.Where(c => c.IsStaged).ToList();
            return staged.Count == 1 ? $"Update {staged[0].FileName}" : "Summary (required)";
        }
    }

    public bool AreAllStaged
    {
        get => Changes.Count > 0 && Changes.All(c => c.IsStaged);
        set
        {
            foreach (var change in Changes)
                change.IsStaged = value;
        }
    }

    public string SelectedCommitFilesLabel => SelectedCommitFiles.Count == 1
        ? "1 file changed"
        : $"{SelectedCommitFiles.Count} files changed";

    /// <summary>Which of the commit's files the diff pane is showing.</summary>
    [ObservableProperty]
    public partial FileChange? SelectedCommitFile { get; set; }

    // ---- Pane widths -------------------------------------------------------
    // Bound two-way so the GridSplitters write back here. The toolbar wordmark
    // reads SidebarWidth too, which is what keeps it flush with the sidebar.

    [ObservableProperty]
    public partial GridLength SidebarWidth { get; set; } = new(400);

    [ObservableProperty]
    public partial GridLength CommitFilesWidth { get; set; } = new(300);

    // ---- Startup -----------------------------------------------------------

    /// <summary>Loads the remembered repositories. Called once after the window opens.</summary>
    public async Task InitialiseAsync()
    {
        if (_isDesignTime)
            return;

        Log(ActivityLevel.Info,
            $"Omnigit ready — {Providers.Count} hosting site{(Providers.Count == 1 ? "" : "s")}: "
            + string.Join(", ", Providers.Select(p => p.DisplayName)));

        Log(ActivityLevel.Trace, _credentials.Description is { } d ? $"Tokens stored in {d}" : "");

        foreach (var warning in _hosts.Warnings)
            Log(ActivityLevel.Warning, warning);

        foreach (var account in await _accountStore.LoadAsync())
            Accounts.Add(account);

        OnPropertyChanged(nameof(HasAccounts));

        foreach (var account in Accounts)
            Log(ActivityLevel.Trace, $"Signed in to {account.BaseUrl.Host} as {account.Login}");

        var paths = await Task.Run(() => _store.Load());

        foreach (var path in paths)
            await AddRepositoryPathAsync(path, persist: false);

        if (Repositories.Count > 0)
            await OpenRepositoryAsync(Repositories[0]);
    }

    // ---- Commands ----------------------------------------------------------

    [RelayCommand]
    private async Task AddRepositoryAsync()
    {
        var path = await _picker.PickAsync("Select a git repository");
        if (string.IsNullOrEmpty(path))
            return;

        if (!await Task.Run(() => _git.IsRepository(path)))
        {
            Log(ActivityLevel.Error, $"'{path}' is not a git repository.");
            return;
        }

        var added = await AddRepositoryPathAsync(path, persist: true);
        if (added is not null)
            await OpenRepositoryAsync(added);
    }

    /// <summary>
    /// Whether the repository list is expanded in the sidebar. It pushes the tabs down
    /// rather than floating over them, so it has to be state the view model owns.
    /// </summary>
    [ObservableProperty]
    public partial bool IsRepositoryPickerOpen { get; set; }

    [RelayCommand]
    private void ToggleRepositoryPicker() => IsRepositoryPickerOpen = !IsRepositoryPickerOpen;

    [RelayCommand]
    private async Task SelectRepositoryAsync(RepositoryInfo repository)
    {
        IsRepositoryPickerOpen = false;
        await OpenRepositoryAsync(repository);
    }

    [RelayCommand]
    private async Task RemoveRepositoryAsync(RepositoryInfo repository)
    {
        Repositories.Remove(repository);
        _store.Save(Repositories.Select(r => r.LocalPath));
        RebuildGroups();

        if (SelectedRepository == repository)
        {
            _watcher.Stop();
            SelectedRepository = null;
            Branches.Clear();
            Changes.Clear();
            History.Clear();
            SelectedCommitFiles.Clear();
        }

        if (Repositories.Count > 0)
            await OpenRepositoryAsync(Repositories[0]);
    }

    [RelayCommand(CanExecute = nameof(CanCreateBranch))]
    private async Task CreateBranchAsync()
    {
        var name = NewBranchName.Trim();
        NewBranchName = string.Empty;

        await BeginBranchSwitchAsync(name, create: true);
    }

    /// <summary>
    /// Switching or creating a branch. With uncommitted work in the tree, the user is
    /// asked what should happen to it first - git would silently carry it across, which
    /// is right often enough to be the default but wrong often enough to be worth asking.
    /// </summary>
    /// <param name="startPoint">
    /// Where a created branch begins. Null branches from HEAD, which is what the branch
    /// picker asks for; a sha comes from the history's "create branch from this commit".
    /// </param>
    private async Task BeginBranchSwitchAsync(string targetBranch, bool create, string? startPoint = null)
    {
        if (SelectedRepository is null)
            return;

        if (Changes.Count == 0)
        {
            await PerformBranchSwitchAsync(targetBranch, create, bringPaths: null, startPoint);
            return;
        }

        // What the changes are being carried away from, which is the commit rather than
        // the branch when the branch is being started somewhere further back.
        var from = startPoint is null
            ? SelectedBranch?.Name ?? "this branch"
            : startPoint.Length > 7 ? startPoint[..7] : startPoint;

        PendingBranchSwitch = new BranchSwitchViewModel(from, targetBranch, create, Changes, startPoint);
    }

    [RelayCommand]
    private void CancelBranchSwitch() => PendingBranchSwitch = null;

    // The radio buttons drive these rather than binding two-way, so that picking one
    // can't leave both looking unselected while the other updates.
    [RelayCommand]
    private void BringChanges()
    {
        if (PendingBranchSwitch is { } pending)
            pending.LeaveEverything = false;
    }

    [RelayCommand]
    private void LeaveChanges()
    {
        if (PendingBranchSwitch is { } pending)
            pending.LeaveEverything = true;
    }

    [RelayCommand]
    private async Task ConfirmBranchSwitchAsync()
    {
        if (PendingBranchSwitch is not { } pending)
            return;

        PendingBranchSwitch = null;

        await PerformBranchSwitchAsync(
            pending.TargetBranch, pending.Create, pending.BringPaths(), pending.StartPoint);
    }

    private async Task PerformBranchSwitchAsync(
        string targetBranch, bool create, IReadOnlyList<string>? bringPaths, string? startPoint = null)
    {
        if (SelectedRepository is not { } repo)
            return;

        var path = repo.LocalPath;
        var stashedSomething = bringPaths is not null;

        await RunAsync(async () =>
        {
            var result = await Task.Run(() => _git.SwitchBranch(path, targetBranch, create, bringPaths, startPoint));

            // Refused rather than failed: the working tree is untouched and the user is
            // still on the branch they started on, so say what to do about it.
            if (!result.Succeeded)
            {
                Log(ActivityLevel.Warning, result.Message);
                return;
            }

            Log(ActivityLevel.Success, create
                ? $"Created and switched to branch {targetBranch}"
                : $"Switched to branch {targetBranch}");

            if (stashedSomething)
                Log(ActivityLevel.Info, "Changes left behind were stashed on the previous branch");
        });

        await OpenRepositoryAsync(repo);
    }

    // ---- Stashes -----------------------------------------------------------

    [RelayCommand]
    private async Task RestoreStashAsync()
    {
        if (SelectedRepository is not { } repo || BranchStashes.FirstOrDefault() is not { } stash)
            return;

        var path = repo.LocalPath;
        var index = stash.Index;

        await RunAsync(async () =>
        {
            await Task.Run(() => _git.PopStash(path, index));
            Log(ActivityLevel.Success, "Restored your stashed changes");
        });

        await OpenRepositoryAsync(repo);
    }

    [RelayCommand]
    private async Task DiscardStashAsync()
    {
        if (SelectedRepository is not { } repo || BranchStashes.FirstOrDefault() is not { } stash)
            return;

        var path = repo.LocalPath;
        var index = stash.Index;

        await RunAsync(async () =>
        {
            await Task.Run(() => _git.DropStash(path, index));
            Log(ActivityLevel.Warning, "Discarded the stashed changes");
        });

        await OpenRepositoryAsync(repo);
    }

    [RelayCommand]
    private async Task SelectBranchAsync(BranchInfo branch)
    {
        if (SelectedRepository is null || branch.IsCurrent)
        {
            SelectedBranch = branch;
            return;
        }

        await BeginBranchSwitchAsync(branch.Name, create: false);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedRepository is { } repo)
            await OpenRepositoryAsync(repo);
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        if (SelectedRepository is not { } repo)
            return;

        var paths = Changes.Where(c => c.IsStaged).Select(c => c.Path).ToList();
        var summary = CommitSummary;
        var description = CommitDescription;
        var path = repo.LocalPath;

        var amending = IsAmending;
        var committed = false;

        await RunAsync(async () =>
        {
            if (amending)
            {
                var amended = await Task.Run(() => _git.AmendCommit(path, paths, summary, description));
                Log(ActivityLevel.Success, $"Amended the last commit — now {amended[..7]}");
            }
            else
            {
                var sha = await Task.Run(() => _git.Commit(path, paths, summary, description));
                Log(ActivityLevel.Success,
                    $"Committed {sha[..7]} — {paths.Count} file{(paths.Count == 1 ? "" : "s")}");
            }

            committed = true;
        });

        if (!committed)
            return;

        // Clearing IsAmending would reload the old message into the boxes, so the flag
        // goes down first and the boxes are cleared after.
        IsAmending = false;
        CommitSummary = string.Empty;
        CommitDescription = string.Empty;
        await OpenRepositoryAsync(repo);
    }

    /// <summary>
    /// Performs whatever the sync button says: publish when the remote has never seen
    /// this branch, pull when behind, push when ahead, otherwise fetch.
    /// </summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        if (SelectedRepository is not { } repo)
            return;

        var path = repo.LocalPath;

        // Publishing is a push - to a branch that isn't there yet. Push writes the
        // tracking config on the way, so this only ever happens once per branch.
        var push = CanPublish || (Behind == 0 && Ahead > 0);
        var behind = Behind;

        await RunAsync(async () =>
        {
            var credentials = await Task.Run(() => CredentialsFor(_git.GetRemoteUrl(path)));

            void Trace(string line) => _log.Write(ActivityLevel.Trace, line);

            var result = await Task.Run(() => push ? _git.Push(path, credentials, Trace)
                                            : behind > 0 ? _git.Pull(path, credentials, Trace)
                                            : _git.Fetch(path, credentials, Trace));

            // Being signed out is an ordinary outcome, so it arrives as a result rather
            // than an exception; it still belongs in the error banner, not the status one.
            Log(result.Succeeded ? ActivityLevel.Success : ActivityLevel.Error, result.Message);
        });

        await OpenRepositoryAsync(repo);
    }

    /// <summary>
    /// Finds the signed-in account matching a remote URL's domain and asks its provider
    /// for git credentials. Null is fine - public HTTPS remotes need no sign-in.
    /// </summary>
    private GitCredentials? CredentialsFor(string? remoteUrl)
    {
        if (HostResolver.Parse(remoteUrl) is not { } identity)
            return null;

        var account = Accounts.FirstOrDefault(a =>
            string.Equals(a.BaseUrl.Host, identity.Host.Id, StringComparison.OrdinalIgnoreCase));

        if (account is null)
            return null;

        return _hosts.ById(account.ProviderId)?.GetGitCredentials(account);
    }

    [RelayCommand]
    private async Task SignInWithTokenAsync()
    {
        if (SelectedProvider is not { } provider)
            return;

        if (!Uri.TryCreate(SignInServerUrl, UriKind.Absolute, out var baseUrl))
        {
            Log(ActivityLevel.Error, "Enter a full server URL, including https://");
            return;
        }

        await RunAsync(async () =>
        {
            var account = await provider.SignInWithTokenAsync(baseUrl, SignInToken.Trim(), default);
            await AddAccountAsync(account);

            SignInToken = string.Empty;
            Log(ActivityLevel.Success, $"Signed in to {provider.DisplayName} as {account.Login}");
        });
    }

    [RelayCommand]
    private async Task StartBrowserLoginAsync()
    {
        if (SelectedProvider is not { } provider)
            return;

        if (!Uri.TryCreate(SignInServerUrl, UriKind.Absolute, out var baseUrl))
        {
            Log(ActivityLevel.Error, "Enter a full server URL, including https://");
            return;
        }

        await RunAsync(async () =>
        {
            var login = await provider.StartBrowserLoginAsync(baseUrl, default);
            PendingDeviceLogin = login;
            OnPropertyChanged(nameof(HasPendingDeviceLogin));

            // The code is useless where it is: it has to reach a browser. Put it on the
            // clipboard and open the page, so the common path is paste-and-approve. Both
            // can fail on a bare desktop, hence the panel keeps its own buttons.
            if (await _shell.CopyTextAsync(login.UserCode))
                Log(ActivityLevel.Info, $"Copied the code {login.UserCode} to the clipboard");

            if (!await _shell.OpenUrlAsync(login.VerificationUri))
                Log(ActivityLevel.Warning, $"Couldn't open a browser. Go to {login.VerificationUri} yourself.");

            try
            {
                var account = await provider.CompleteBrowserLoginAsync(baseUrl, login, default);
                await AddAccountAsync(account);
                Log(ActivityLevel.Success, $"Signed in to {provider.DisplayName} as {account.Login}");
            }
            finally
            {
                PendingDeviceLogin = null;
                OnPropertyChanged(nameof(HasPendingDeviceLogin));
            }
        });
    }

    [RelayCommand]
    private async Task OpenDeviceUrlAsync()
    {
        if (PendingDeviceLogin is not { } login)
            return;

        if (!await _shell.OpenUrlAsync(login.VerificationUri))
            Log(ActivityLevel.Warning, $"Couldn't open a browser. Go to {login.VerificationUri} yourself.");
    }

    [RelayCommand]
    private async Task CopyDeviceCodeAsync()
    {
        if (PendingDeviceLogin is not { } login)
            return;

        if (await _shell.CopyTextAsync(login.UserCode))
            Log(ActivityLevel.Info, $"Copied {login.UserCode} to the clipboard");
        else
            Log(ActivityLevel.Warning, "Couldn't reach the clipboard.");
    }

    [RelayCommand]
    private async Task SignOutAsync(HostAccount account)
    {
        await RunAsync(async () =>
        {
            await _accountStore.RemoveAsync(account);
            Accounts.Remove(account);
            OnPropertyChanged(nameof(HasAccounts));
            Log(ActivityLevel.Info, $"Signed out {account.Login}");
        });
    }

    private async Task AddAccountAsync(HostAccount account)
    {
        await _accountStore.SaveAsync(account);

        // Signing in again with a fresh token replaces the old entry.
        if (Accounts.FirstOrDefault(a => a.Key == account.Key) is { } existing)
            Accounts.Remove(existing);

        Accounts.Add(account);
        OnPropertyChanged(nameof(HasAccounts));
    }

    [RelayCommand]
    private void ShowChangesTab() => SelectedTabIndex = 0;

    [RelayCommand]
    private void ShowHistoryTab() => SelectedTabIndex = 1;

    // ---- Commit history context menu ---------------------------------------

    /// <summary>
    /// Only ever the newest commit. Amending an older one means rewriting everything
    /// after it, which is an interactive rebase and not something this app does.
    /// </summary>
    public bool CanAmendSelectedCommit => CanAmend
                                          && SelectedCommit is { } commit
                                          && History.FirstOrDefault()?.Sha == commit.Sha;

    /// <summary>
    /// Hands over to the commit box: that is where the message is edited and files are
    /// staged, so amending has to happen on the Changes tab whatever started it. Ticking
    /// the flag is what loads the old message into the boxes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAmendSelectedCommit))]
    private void AmendSelectedCommit()
    {
        SelectedTabIndex = 0;
        IsAmending = true;
    }

    /// <summary>Setting the flag down is what clears the loaded message out of the boxes.</summary>
    [RelayCommand]
    private void CancelAmend() => IsAmending = false;

    private bool HasSelectedCommit => SelectedCommit is not null;

    /// <summary>
    /// Reverting and resetting both need a branch to move or to write onto, and a
    /// detached HEAD has neither.
    /// </summary>
    private bool CanChangeHistoryHere => SelectedCommit is not null && !IsDetachedHead;

    private bool CanCopyCommitTag => SelectedCommit?.HasTags == true;

    [RelayCommand(CanExecute = nameof(HasSelectedCommit))]
    private async Task CopyCommitShaAsync()
    {
        if (SelectedCommit is not { } commit)
            return;

        if (await _shell.CopyTextAsync(commit.Sha))
            Log(ActivityLevel.Info, $"Copied {commit.ShortSha} to the clipboard");
        else
            Log(ActivityLevel.Warning, "Could not reach the clipboard");
    }

    [RelayCommand(CanExecute = nameof(HasSelectedCommit))]
    private async Task CopyCommitSummaryAsync()
    {
        if (SelectedCommit is not { } commit)
            return;

        if (await _shell.CopyTextAsync(commit.Summary))
            Log(ActivityLevel.Info, "Copied the commit summary to the clipboard");
        else
            Log(ActivityLevel.Warning, "Could not reach the clipboard");
    }

    /// <summary>Every tag on the commit, since a commit can carry more than one.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyCommitTag))]
    private async Task CopyCommitTagAsync()
    {
        if (SelectedCommit is not { Tags.Count: > 0 } commit)
            return;

        await CopyAsync(string.Join(" ", commit.Tags), commit.Tags.Count == 1 ? "tag" : "tags");
    }

    // ---- Opening the commit on the site it came from ------------------------

    public bool CanViewCommitOnHost => SelectedCommit is not null && CommitUrl(SelectedCommit) is not null;

    /// <summary>Names the site, the way GitHub Desktop's "View on GitHub" does.</summary>
    public string ViewOnHostLabel =>
        $"View on {(SelectedRepository?.Host is { BaseUrl.Length: > 0 } host ? host.Name : "the hosting site")}";

    /// <summary>
    /// Where this commit lives on the web. A site we are signed in to describes its own
    /// URL shape; for one we aren't, the shape nearly everything uses is still a better
    /// answer than no link at all.
    /// </summary>
    private Uri? CommitUrl(CommitInfo commit)
    {
        if (SelectedRepository is not { Host.BaseUrl.Length: > 0 } repo)
            return null;

        var account = Accounts.FirstOrDefault(a =>
            string.Equals(a.BaseUrl.Host, repo.Host.Id, StringComparison.OrdinalIgnoreCase));

        var template = account is null ? null : _hosts.ById(account.ProviderId)?.CommitUrlTemplate;

        if (account?.BaseUrl is { } signedInUrl)
            return WebLinks.CommitUrl(signedInUrl, repo.Owner, repo.Name, commit.Sha, template);

        return Uri.TryCreate(repo.Host.BaseUrl, UriKind.Absolute, out var baseUrl)
            ? WebLinks.CommitUrl(baseUrl, repo.Owner, repo.Name, commit.Sha, template)
            : null;
    }

    [RelayCommand(CanExecute = nameof(CanViewCommitOnHost))]
    private async Task ViewCommitOnHostAsync()
    {
        if (SelectedCommit is not { } commit || CommitUrl(commit) is not { } url)
            return;

        if (!await _shell.OpenUrlAsync(url))
            Log(ActivityLevel.Warning, $"Couldn't open a browser. The commit is at {url}");
    }

    // ---- Branching and tagging from a commit -------------------------------

    [RelayCommand(CanExecute = nameof(HasSelectedCommit))]
    private void BranchFromSelectedCommit()
    {
        if (SelectedCommit is { } commit)
            BranchFromCommit = new BranchFromCommitViewModel(commit);
    }

    /// <summary>
    /// The way back out of a detached HEAD: the commit sitting there becomes a branch.
    /// History is walked from HEAD, so its first entry is the commit we are on.
    /// </summary>
    [RelayCommand]
    private void BranchFromHere()
    {
        if (History.FirstOrDefault() is { } head)
            BranchFromCommit = new BranchFromCommitViewModel(head);
    }

    [RelayCommand]
    private void CancelBranchFromCommit() => BranchFromCommit = null;

    [RelayCommand]
    private async Task ConfirmBranchFromCommitAsync()
    {
        if (BranchFromCommit is not { CanCreate: true } draft)
            return;

        var (name, sha) = (draft.Name.Trim(), draft.Sha);
        BranchFromCommit = null;

        await BeginBranchSwitchAsync(name, create: true, startPoint: sha);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedCommit))]
    private void TagSelectedCommit()
    {
        if (SelectedCommit is { } commit)
            TagDraft = new TagDraftViewModel(commit);
    }

    [RelayCommand]
    private void CancelTag() => TagDraft = null;

    [RelayCommand]
    private async Task ConfirmTagAsync()
    {
        if (TagDraft is not { CanCreate: true } draft || SelectedRepository is not { } repo)
            return;

        var (name, message, sha) = (draft.Name.Trim(), draft.Message, draft.Sha);
        var path = repo.LocalPath;
        TagDraft = null;

        await RunAsync(async () =>
        {
            var created = await Task.Run(() => _git.CreateTag(path, name, sha, message));

            Log(ActivityLevel.Success, $"Tagged {sha[..7]} as {created}"
                                       + (string.IsNullOrWhiteSpace(message) ? string.Empty : " (annotated)"));
        });

        await OpenRepositoryAsync(repo);
    }

    // ---- Opening an older commit -------------------------------------------

    [RelayCommand(CanExecute = nameof(HasSelectedCommit))]
    private async Task CheckoutSelectedCommitAsync()
    {
        if (SelectedCommit is not { } commit || SelectedRepository is not { } repo)
            return;

        var path = repo.LocalPath;
        var sha = commit.Sha;

        await RunAsync(async () =>
        {
            var result = await Task.Run(() => _git.CheckoutCommit(path, sha));

            if (!result.Succeeded)
            {
                Log(ActivityLevel.Warning, result.Message);
                return;
            }

            Log(ActivityLevel.Info,
                $"Opened {commit.ShortSha}. You are not on a branch — make one here, or pick a "
                + "branch to go back.");
        });

        await OpenRepositoryAsync(repo);
    }

    // ---- Undoing and copying commits ---------------------------------------

    [RelayCommand(CanExecute = nameof(CanChangeHistoryHere))]
    private async Task RevertSelectedCommitAsync()
    {
        if (SelectedCommit is not { } commit || SelectedRepository is not { } repo)
            return;

        var path = repo.LocalPath;
        var sha = commit.Sha;

        await RunAsync(async () =>
        {
            ReportOperation(await Task.Run(() => _git.RevertCommit(path, sha)));
        });

        await OpenRepositoryAsync(repo);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedCommit))]
    private void CherryPickSelectedCommit()
    {
        if (SelectedCommit is { } commit)
        {
            CherryPickDraft = new CherryPickDraftViewModel(
                commit, Branches.Where(b => !b.IsCurrent).Select(b => b.Name));
        }
    }

    [RelayCommand]
    private void CancelCherryPick() => CherryPickDraft = null;

    [RelayCommand]
    private async Task ConfirmCherryPickAsync()
    {
        if (CherryPickDraft is not { CanApply: true } draft || SelectedRepository is not { } repo)
            return;

        var (sha, onto) = (draft.Sha, draft.TargetBranch!);
        var path = repo.LocalPath;
        CherryPickDraft = null;

        await RunAsync(async () =>
        {
            ReportOperation(await Task.Run(() => _git.CherryPickCommit(path, sha, onto)));
        });

        await OpenRepositoryAsync(repo);
    }

    // ---- Moving the branch back --------------------------------------------

    [RelayCommand(CanExecute = nameof(CanChangeHistoryHere))]
    private void ResetToSelectedCommit()
    {
        if (SelectedCommit is { } commit)
            ResetDraft = new ResetDraftViewModel(commit);
    }

    [RelayCommand]
    private void CancelReset() => ResetDraft = null;

    [RelayCommand]
    private async Task ConfirmResetAsync()
    {
        if (ResetDraft is not { } draft || SelectedRepository is not { } repo)
            return;

        var (sha, kind) = (draft.Sha, draft.Kind);
        var path = repo.LocalPath;
        ResetDraft = null;

        await RunAsync(async () =>
        {
            await Task.Run(() => _git.ResetToCommit(path, sha, kind));

            Log(kind == ResetKind.Hard ? ActivityLevel.Warning : ActivityLevel.Success,
                kind switch
                {
                    ResetKind.Soft => $"Moved the branch back to {sha[..7]} — the changes are staged",
                    ResetKind.Hard => $"Reset to {sha[..7]} — everything after it was discarded",
                    _ => $"Moved the branch back to {sha[..7]} — the changes are in your working tree",
                });
        });

        await OpenRepositoryAsync(repo);
    }

    /// <summary>
    /// Reports how one of these ended. Conflicts move the user to the Changes tab, since
    /// that is where the panel that resolves them lives and there is nothing else to do
    /// until they are dealt with.
    /// </summary>
    private void ReportOperation(CommitOperationResult result)
    {
        Log(result.Outcome switch
        {
            CommitOperationOutcome.Succeeded => ActivityLevel.Success,
            CommitOperationOutcome.NothingToDo => ActivityLevel.Info,
            _ => ActivityLevel.Warning,
        }, result.Message);

        if (result.HasConflicts)
            SelectedTabIndex = 0;
    }

    // ---- Finishing what git could not --------------------------------------

    [RelayCommand]
    private Task KeepMineAsync(string? path) => ResolveAsync(path, ConflictSide.Mine);

    [RelayCommand]
    private Task KeepTheirsAsync(string? path) => ResolveAsync(path, ConflictSide.Theirs);

    private async Task ResolveAsync(string? file, ConflictSide side)
    {
        if (string.IsNullOrEmpty(file) || SelectedRepository is not { } repo)
            return;

        var local = repo.LocalPath;

        await RunAsync(async () =>
        {
            await Task.Run(() => _git.ResolveConflict(local, file, side));

            Log(ActivityLevel.Info, side == ConflictSide.Mine
                ? $"Kept your version of {file}"
                : $"Took the incoming version of {file}");
        });

        await OpenRepositoryAsync(repo);
    }

    [RelayCommand]
    private async Task MarkResolvedAsync(string? file)
    {
        if (string.IsNullOrEmpty(file) || SelectedRepository is not { } repo)
            return;

        var local = repo.LocalPath;

        await RunAsync(async () =>
        {
            await Task.Run(() => _git.MarkConflictResolved(local, [file]));
            Log(ActivityLevel.Info, $"Marked {file} as resolved");
        });

        await OpenRepositoryAsync(repo);
    }

    [RelayCommand]
    private void AskAbortOperation() => IsConfirmingAbort = true;

    [RelayCommand]
    private void CancelAbort() => IsConfirmingAbort = false;

    [RelayCommand]
    private async Task ConfirmAbortAsync()
    {
        if (SelectedRepository is not { } repo)
            return;

        var what = PendingOperationName;
        var path = repo.LocalPath;
        IsConfirmingAbort = false;

        await RunAsync(async () =>
        {
            await Task.Run(() => _git.AbortOperation(path));
            Log(ActivityLevel.Warning, $"Abandoned the {what} — everything is back as it was");
        });

        // The message git prepared belongs to the operation that no longer exists.
        CommitSummary = string.Empty;
        CommitDescription = string.Empty;

        await OpenRepositoryAsync(repo);
    }

    // ---- Browse and clone --------------------------------------------------

    [RelayCommand]
    private async Task ShowCloneAsync()
    {
        IsClonePageVisible = true;

        if (_allRemotes.Count == 0)
            await LoadRemoteRepositoriesAsync();
    }

    [RelayCommand]
    private void CloseClone() => IsClonePageVisible = false;

    /// <summary>
    /// Asks every signed-in account what it can see. Sites are queried in parallel
    /// because one slow server shouldn't hold up the rest of the list.
    /// </summary>
    [RelayCommand]
    private async Task LoadRemoteRepositoriesAsync()
    {
        if (IsLoadingRemotes)
            return;

        IsLoadingRemotes = true;

        try
        {
            var known = Repositories
                .Select(r => _git.GetRemoteUrl(r.LocalPath))
                .Where(u => u is not null)
                .Select(NormaliseUrl!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var loaded = new List<RemoteRepositoryViewModel>();

            var lookups = Accounts
                .Select(account => (account, provider: _hosts.ById(account.ProviderId)))
                .Where(pair => pair.provider is not null)
                .Select(async pair =>
                {
                    try
                    {
                        var repositories = await pair.provider!.ListRepositoriesAsync(pair.account, default);
                        return (pair.account, repositories, error: (string?)null);
                    }
                    catch (Exception ex)
                    {
                        // One unreachable site must not empty the whole list.
                        return (pair.account, (IReadOnlyList<RemoteRepository>)[], error: ex.Message);
                    }
                })
                .ToList();

            foreach (var (account, repositories, error) in await Task.WhenAll(lookups))
            {
                if (error is not null)
                {
                    Log(ActivityLevel.Warning, $"Could not list repositories for {account.Handle}: {error}");
                    continue;
                }

                foreach (var repository in repositories)
                {
                    loaded.Add(new RemoteRepositoryViewModel(
                        repository, account, known.Contains(NormaliseUrl(repository.CloneUrl))));
                }
            }

            _allRemotes.Clear();
            _allRemotes.AddRange(loaded
                .OrderByDescending(r => r.Model.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase));

            ApplyRemoteFilter();

            if (_allRemotes.Count > 0)
                Log(ActivityLevel.Info, $"Found {_allRemotes.Count} repositories you can clone");
        }
        finally
        {
            IsLoadingRemotes = false;
        }
    }

    /// <summary>
    /// Clone URLs vary by spelling for the same repository - trailing .git, scp form,
    /// case - so they are compared through <see cref="HostResolver"/> instead.
    /// </summary>
    private static string NormaliseUrl(string url)
        => HostResolver.Parse(url) is { } identity
            ? $"{identity.Host.Id}/{identity.Owner}/{identity.Name}"
            : url;

    private void ApplyRemoteFilter()
    {
        var filter = RemoteFilter.Trim();

        RemoteRepositories.Clear();

        foreach (var repository in _allRemotes)
        {
            if (filter.Length == 0
                || repository.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || repository.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                RemoteRepositories.Add(repository);
            }
        }

        OnPropertyChanged(nameof(HasRemoteResults));
        OnPropertyChanged(nameof(RemoteEmptyLabel));
    }

    [RelayCommand]
    private async Task CloneRepositoryAsync(RemoteRepositoryViewModel repository)
    {
        var parent = await _picker.PickAsync($"Where should {repository.Name} go?");
        if (string.IsNullOrEmpty(parent))
            return;

        var target = System.IO.Path.Combine(parent, repository.Name);
        var url = repository.CloneUrl;
        var credentials = _hosts.ById(repository.Account.ProviderId)
            ?.GetGitCredentials(repository.Account);

        var cloned = false;

        await RunAsync(async () =>
        {
            void Trace(string line) => _log.Write(ActivityLevel.Trace, line);

            var result = await Task.Run(() => _git.Clone(url, target, credentials, Trace));

            Log(result.Succeeded ? ActivityLevel.Success : ActivityLevel.Error, result.Message);
            cloned = result.Succeeded;
        });

        if (!cloned)
            return;

        var added = await AddRepositoryPathAsync(target, persist: true);

        IsClonePageVisible = false;

        if (added is not null)
            await OpenRepositoryAsync(added);

        // The row should now offer to open rather than clone again.
        await LoadRemoteRepositoriesAsync();
    }

    [RelayCommand]
    private void ShowSettings()
    {
        RefreshHostEntries();
        IsClonePageVisible = false;
        IsSettingsPageVisible = true;
    }

    [RelayCommand]
    private void ShowRepository()
    {
        IsSettingsPageVisible = false;
        IsClonePageVisible = false;
    }

    [RelayCommand]
    private void ShowSettingsSection(int section)
    {
        SettingsSection = section;
        HostDraft = null;
    }

    // ---- Hosts -------------------------------------------------------------

    [RelayCommand]
    private void AddHost() => HostDraft = HostDraftViewModel.GiteaLike();

    [RelayCommand]
    private void AddGitLabLikeHost() => HostDraft = HostDraftViewModel.GitLabLike();

    [RelayCommand]
    private void EditHost(HostEntryViewModel entry)
    {
        if (_hosts.LoadUserManifest(entry.Id) is not { } manifest)
        {
            Log(ActivityLevel.Error, $"Could not read the description for '{entry.Id}'.");
            return;
        }

        HostDraft = HostDraftViewModel.FromManifest(manifest);
    }

    [RelayCommand]
    private void CancelHostDraft() => HostDraft = null;

    /// <summary>
    /// Tries the draft against a real server before it is saved. Sign-in used to be the
    /// first thing that touched these endpoints, which meant a typo showed up as a
    /// failed login with nothing pointing at the field that caused it.
    /// </summary>
    [RelayCommand]
    private async Task TestHostAsync()
    {
        if (HostDraft is not { CanTest: true } draft)
            return;

        if (!HostConnectionTester.TryParseBaseUrl(draft.TestUrl, out var baseUrl))
        {
            Log(ActivityLevel.Error, "Enter the address of a server to test against.");
            return;
        }

        draft.IsTesting = true;

        try
        {
            var report = await _hosts.TestAsync(draft.ToManifest(), baseUrl, draft.TestToken, default);
            draft.ShowTestResult(report);

            foreach (var step in report.Steps)
            {
                Log(step.Outcome switch
                    {
                        ProbeOutcome.Passed => ActivityLevel.Trace,
                        ProbeOutcome.Failed => ActivityLevel.Warning,
                        _ => ActivityLevel.Trace,
                    },
                    $"{baseUrl.Host} — {step.Name}: {step.Detail}");
            }
        }
        catch (Exception ex)
        {
            Log(ActivityLevel.Error, $"Could not test the site: {ex.Message}", ex.ToString());
        }
        finally
        {
            draft.IsTesting = false;
        }
    }

    [RelayCommand]
    private void SaveHost()
    {
        if (HostDraft is not { CanSave: true } draft)
            return;

        try
        {
            _hosts.SaveUserManifest(draft.ToManifest());
            AfterHostsChanged($"Saved the '{draft.DisplayName}' hosting site");
            HostDraft = null;
        }
        catch (Exception ex)
        {
            Log(ActivityLevel.Error, $"Could not save the host: {ex.Message}", ex.ToString());
        }
    }

    [RelayCommand]
    private void DeleteHost(HostEntryViewModel entry)
    {
        try
        {
            _hosts.DeleteUserManifest(entry.Id);
            AfterHostsChanged($"Removed the '{entry.DisplayName}' hosting site");
        }
        catch (Exception ex)
        {
            Log(ActivityLevel.Error, $"Could not remove the host: {ex.Message}", ex.ToString());
        }
    }

    /// <summary>
    /// The registry reloaded, so every collection holding providers is now stale - the
    /// sign-in picker included, which would otherwise keep a deleted site selected.
    /// </summary>
    private void AfterHostsChanged(string message)
    {
        var previouslySelected = SelectedProvider?.Id;

        Providers.Clear();
        foreach (var provider in _hosts.Providers)
            Providers.Add(provider);

        SelectedProvider = Providers.FirstOrDefault(p => p.Id == previouslySelected)
                           ?? Providers.FirstOrDefault();

        RefreshHostEntries();
        OnPropertyChanged(nameof(HostWarnings));
        OnPropertyChanged(nameof(HasHostWarnings));

        Log(ActivityLevel.Success, message);
    }

    private void RefreshHostEntries()
    {
        HostEntries.Clear();

        foreach (var provider in _hosts.Providers)
            HostEntries.Add(new HostEntryViewModel(provider, _hosts.IsUserDefined(provider.Id)));
    }

    // ---- Loading -----------------------------------------------------------

    private async Task<RepositoryInfo?> AddRepositoryPathAsync(string path, bool persist)
    {
        if (Repositories.Any(r => string.Equals(r.LocalPath, path, StringComparison.Ordinal)))
            return null;

        RepositoryInfo info;
        try
        {
            info = await Task.Run(() => _git.OpenRepository(path));
        }
        catch (Exception ex)
        {
            Log(ActivityLevel.Error, $"Could not open '{path}'", ex.Message);
            return null;
        }

        // A repo discovered from a subdirectory resolves to its working-tree root,
        // which may already be in the list.
        if (Repositories.Any(r => string.Equals(r.LocalPath, info.LocalPath, StringComparison.Ordinal)))
            return null;

        Repositories.Add(info);
        RebuildGroups();
        OnPropertyChanged(nameof(HasRepositories));

        if (persist)
            _store.Save(Repositories.Select(r => r.LocalPath));

        return info;
    }

    private async Task OpenRepositoryAsync(RepositoryInfo repository)
    {
        SelectedRepository = repository;
        OnPropertyChanged(nameof(SyncDetailLabel));
        _watcher.Watch(repository.LocalPath);

        await RunAsync(() => LoadRepositoryAsync(repository, announce: true));
    }

    /// <summary>
    /// Something changed on disk under the repository - an editor saved, or git ran in a
    /// terminal. Reload without the busy strip and without disturbing what the user is in
    /// the middle of, since they did not ask for this.
    /// </summary>
    private async void OnRepositoryChangedOnDisk(object? sender, EventArgs e)
    {
        // Our own git operation is already going to reload when it finishes.
        if (IsBusy || SelectedRepository is not { } repository)
            return;

        try
        {
            await LoadRepositoryAsync(repository, announce: false);
        }
        catch (Exception ex)
        {
            // An automatic refresh is not worth interrupting anyone over; the next
            // deliberate action will surface the problem properly.
            Log(ActivityLevel.Trace, $"Automatic refresh failed: {ex.Message}");
        }
    }

    /// <param name="announce">
    /// False for automatic refreshes: keeps the log quiet and preserves the user's
    /// selection and tick state instead of resetting to defaults.
    /// </param>
    private async Task LoadRepositoryAsync(RepositoryInfo repository, bool announce)
    {
        var path = repository.LocalPath;

        // Captured before the reload so they can be re-applied to the new instances.
        var knownPaths = Changes.Select(c => c.Path).ToHashSet();
        var stagedPaths = Changes.Where(c => c.IsStaged).Select(c => c.Path).ToHashSet();
        var selectedChangePath = SelectedChange?.Path;
        var selectedSha = SelectedCommit?.Sha;
        var selectedCommitFilePath = SelectedCommitFile?.Path;

        var (info, branches, changes, history, stashes, conflicts) = await Task.Run(() => (
            _git.OpenRepository(path),
            _git.GetBranches(path),
            _git.GetWorkingChanges(path),
            _git.GetHistory(path, HistoryLimit),
            _git.GetStashes(path),
            _git.GetConflictedPaths(path)));

        Ahead = info.Ahead;
        Behind = info.Behind;
        HasRemote = info.HasRemote;
        IsPublished = info.IsPublished;
        LastFetched = info.LastFetched;
        IsDetachedHead = info.IsDetached;
        HeadShortSha = info.HeadShortSha;

        PendingOperation = info.Operation;
        Replace(ConflictedPaths, conflicts);
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(PendingOperationLabel));

        // git writes the message for the commit that finishes a merge or a revert. Only
        // offered into an empty box, so an automatic refresh can't overwrite typing.
        if (info.Operation != RepositoryOperation.None
            && string.IsNullOrWhiteSpace(CommitSummary)
            && !IsAmending
            && await Task.Run(() => _git.GetPendingMessage(path)) is { } prepared)
        {
            CommitSummary = prepared.Summary;
            CommitDescription = prepared.Description;
        }

        Replace(Branches, branches);
        Replace(History, history);

        foreach (var change in Changes)
            change.PropertyChanged -= OnChangePropertyChanged;

        Changes.Clear();
        foreach (var change in changes)
        {
            var vm = new FileChangeViewModel(change);

            // A file we already knew about keeps its tick; anything new arrives ticked,
            // which is what a fresh listing would have done anyway.
            if (!announce)
                vm.IsStaged = !knownPaths.Contains(vm.Path) || stagedPaths.Contains(vm.Path);

            vm.PropertyChanged += OnChangePropertyChanged;
            Changes.Add(vm);
        }

        SelectedBranch = Branches.FirstOrDefault(b => b.IsCurrent) ?? Branches.FirstOrDefault();

        // Only the current branch's stashes, since that's all the commit box can offer
        // to restore without switching first.
        BranchStashes.Clear();
        foreach (var stash in stashes.Where(s => s.BranchName == SelectedBranch?.Name))
            BranchStashes.Add(stash);

        OnPropertyChanged(nameof(HasBranchStashes));
        OnPropertyChanged(nameof(StashLabel));

        SelectedChange = (announce ? null : Changes.FirstOrDefault(c => c.Path == selectedChangePath))
                         ?? Changes.FirstOrDefault();

        SelectedCommit = (announce ? null : History.FirstOrDefault(c => c.Sha == selectedSha))
                         ?? History.FirstOrDefault();

        // Reloading the commit replaces its file list, so restore the chosen file once
        // that has happened rather than now.
        if (!announce && selectedCommitFilePath is not null)
            _restoreCommitFilePath = selectedCommitFilePath;

        NotifyChangeCountsChanged();

        if (!announce)
            return;

        Log(ActivityLevel.Trace,
            $"Opened {repository.Name} on {SelectedBranch?.Name ?? "?"} — "
            + $"{Changes.Count} change{(Changes.Count == 1 ? "" : "s")}, "
            + $"{Branches.Count} branch{(Branches.Count == 1 ? "" : "es")}"
            + (info.Ahead > 0 ? $", {info.Ahead} ahead" : string.Empty)
            + (info.Behind > 0 ? $", {info.Behind} behind" : string.Empty));
    }

    /// <summary>Commit diffs are loaded only when a commit is actually selected.</summary>
    partial void OnSelectedCommitChanged(CommitInfo? value)
    {
        NotifyCommitCommandsChanged();

        if (_isDesignTime || value is null || SelectedRepository is not { } repo)
        {
            SelectedCommitFiles.Clear();
            OnPropertyChanged(nameof(SelectedCommitFilesLabel));
            return;
        }

        _ = LoadCommitFilesAsync(repo.LocalPath, value.Sha);
    }

    private async Task LoadCommitFilesAsync(string path, string sha)
    {
        try
        {
            var files = await Task.Run(() => _git.GetCommitFiles(path, sha));

            // The user may have clicked another commit while this was loading.
            if (SelectedCommit?.Sha != sha)
                return;

            Replace(SelectedCommitFiles, files);
            OnPropertyChanged(nameof(SelectedCommitFilesLabel));

            // Show the first file's diff rather than an empty pane - unless an automatic
            // refresh asked to put the user back on the file they were reading.
            SelectedCommitFile =
                (_restoreCommitFilePath is { } wanted
                    ? SelectedCommitFiles.FirstOrDefault(f => f.Path == wanted)
                    : null)
                ?? SelectedCommitFiles.FirstOrDefault();

            _restoreCommitFilePath = null;
        }
        catch (Exception ex)
        {
            Log(ActivityLevel.Error, ex.Message, ex.ToString());
        }
    }

    /// <summary>Runs a git operation with busy state and error capture around it.</summary>
    private async Task RunAsync(Func<Task> operation)
    {
        IsBusy = true;
        CommitCommand.NotifyCanExecuteChanged();

        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            // Unexpected faults still reach the user rather than vanishing.
            Log(ActivityLevel.Error, ex.Message, ex.ToString());
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanCommit));
            CommitCommand.NotifyCanExecuteChanged();
        }
    }

    private void RebuildGroups()
    {
        var groups = Repositories
            .GroupBy(r => r.Host)
            .Select(g => new HostGroupViewModel { Host = g.Key, Repositories = g.ToList() })
            .OrderBy(g => g.Host.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Replace(RepositoryGroups, groups);
        Replace(Hosts, Repositories.Select(r => r.Host).Distinct().ToList());
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private void OnChangePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileChangeViewModel.IsStaged))
            NotifyChangeCountsChanged();
    }

    /// <summary>
    /// Everything in the history's context menu acts on the selected commit. Done in one
    /// place rather than as a stack of attributes on the property, which by the ninth
    /// entry says less about what the menu offers than this does.
    /// </summary>
    private void NotifyCommitCommandsChanged()
    {
        CopyCommitShaCommand.NotifyCanExecuteChanged();
        CopyCommitSummaryCommand.NotifyCanExecuteChanged();
        CopyCommitTagCommand.NotifyCanExecuteChanged();
        ViewCommitOnHostCommand.NotifyCanExecuteChanged();
        BranchFromSelectedCommitCommand.NotifyCanExecuteChanged();
        TagSelectedCommitCommand.NotifyCanExecuteChanged();
        CheckoutSelectedCommitCommand.NotifyCanExecuteChanged();
        RevertSelectedCommitCommand.NotifyCanExecuteChanged();
        CherryPickSelectedCommitCommand.NotifyCanExecuteChanged();
        ResetToSelectedCommitCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(CanViewCommitOnHost));
        OnPropertyChanged(nameof(ViewOnHostLabel));
    }

    private void NotifyChangeCountsChanged()
    {
        OnPropertyChanged(nameof(StagedCount));
        OnPropertyChanged(nameof(StagedCountLabel));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(AreAllStaged));
        OnPropertyChanged(nameof(CommitSummaryPlaceholder));
        CommitCommand.NotifyCanExecuteChanged();
    }

    private void LoadDesignTimeData()
    {
        Replace(Repositories, MockData.Repositories);
        Replace(Branches, MockData.Branches);
        Replace(History, MockData.History);
        Replace(Hosts, MockData.Hosts);

        foreach (var change in MockData.WorkingChanges)
            Changes.Add(new FileChangeViewModel(change));

        RebuildGroups();
        SelectedRepository = Repositories.FirstOrDefault();
        SelectedBranch = Branches.FirstOrDefault();
        SelectedChange = Changes.FirstOrDefault();
        SelectedCommit = History.FirstOrDefault();
    }
}
