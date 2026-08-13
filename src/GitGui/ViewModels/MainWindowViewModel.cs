using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls; // GridLength, for the resizable pane widths below.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitGui.HostProviders;
using GitGui.Models;
using GitGui.Services;

namespace GitGui.ViewModels;

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
    /// Depends on the server as well as the provider: GitGui's built-in client id is
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
    public partial BranchInfo? SelectedBranch { get; set; }

    [ObservableProperty]
    public partial FileChangeViewModel? SelectedChange { get; set; }

    [ObservableProperty]
    public partial CommitInfo? SelectedCommit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChangesTab))]
    [NotifyPropertyChangedFor(nameof(IsHistoryTab))]
    public partial int SelectedTabIndex { get; set; }

    public bool IsChangesTab => SelectedTabIndex == 0;
    public bool IsHistoryTab => SelectedTabIndex == 1;

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

    /// <summary>Every site GitGui knows about, whatever the description came from.</summary>
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
    [NotifyPropertyChangedFor(nameof(AmendHint))]
    public partial int Ahead { get; set; }

    /// <summary>False when the branch has never been pushed; see <see cref="CanAmend"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAmend))]
    [NotifyPropertyChangedFor(nameof(AmendHint))]
    public partial bool HasUpstream { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncActionLabel))]
    [NotifyPropertyChangedFor(nameof(SyncCountLabel))]
    [NotifyPropertyChangedFor(nameof(HasSyncCount))]
    public partial int Behind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncDetailLabel))]
    public partial DateTimeOffset? LastFetched { get; set; }

    public string SyncActionLabel => Behind > 0 ? "Pull origin"
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
    // file is a perfectly ordinary thing to want.
    public bool CanCommit => (StagedCount > 0 || IsAmending)
                             && !string.IsNullOrWhiteSpace(CommitSummary)
                             && !IsBusy;

    /// <summary>
    /// Amending rewrites history, so it is offered only while the last commit is still
    /// local. Once pushed, changing it would need a force-push, which is not something
    /// to make available behind a checkbox.
    /// </summary>
    public bool CanAmend => SelectedRepository is not null
                            && (Ahead > 0 || !HasUpstream);

    public string AmendHint => CanAmend
        ? "Replace the last commit instead of adding one."
        : "The last commit is already pushed, so it can't be amended here.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    [NotifyPropertyChangedFor(nameof(CommitButtonLabel))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial bool IsAmending { get; set; }

    /// <summary>Loads or clears the previous message as the checkbox is ticked.</summary>
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

    public bool CanCreateBranch => !string.IsNullOrWhiteSpace(NewBranchName)
                                   && SelectedRepository is not null
                                   && !IsBusy;

    public string CommitButtonLabel => IsAmending
        ? "Amend last commit"
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
    public partial GridLength SidebarWidth { get; set; } = new(340);

    [ObservableProperty]
    public partial GridLength CommitFilesWidth { get; set; } = new(300);

    // ---- Startup -----------------------------------------------------------

    /// <summary>Loads the remembered repositories. Called once after the window opens.</summary>
    public async Task InitialiseAsync()
    {
        if (_isDesignTime)
            return;

        Log(ActivityLevel.Info,
            $"GitGui ready — {Providers.Count} hosting site{(Providers.Count == 1 ? "" : "s")}: "
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

    [RelayCommand]
    private async Task SelectRepositoryAsync(RepositoryInfo repository)
        => await OpenRepositoryAsync(repository);

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
        if (SelectedRepository is not { } repo)
            return;

        var path = repo.LocalPath;
        var name = NewBranchName;

        await RunAsync(async () =>
        {
            var created = await Task.Run(() => _git.CreateBranch(path, name));
            Log(ActivityLevel.Success, $"Created and switched to branch {created}");
        });

        NewBranchName = string.Empty;
        await OpenRepositoryAsync(repo);
    }

    [RelayCommand]
    private async Task SelectBranchAsync(BranchInfo branch)
    {
        if (SelectedRepository is not { } repo || branch.IsCurrent)
        {
            SelectedBranch = branch;
            return;
        }

        await RunAsync(async () =>
        {
            var path = repo.LocalPath;
            await Task.Run(() => _git.CheckoutBranch(path, branch.Name));
            Log(ActivityLevel.Success, $"Switched to branch {branch.Name}");
        });

        await OpenRepositoryAsync(repo);
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
    /// Performs whatever the sync button says: pull when behind, push when ahead,
    /// otherwise fetch.
    /// </summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        if (SelectedRepository is not { } repo)
            return;

        var path = repo.LocalPath;
        var behind = Behind;
        var ahead = Ahead;

        await RunAsync(async () =>
        {
            var credentials = await Task.Run(() => CredentialsFor(_git.GetRemoteUrl(path)));

            void Trace(string line) => _log.Write(ActivityLevel.Trace, line);

            var result = await Task.Run(() => behind > 0 ? _git.Pull(path, credentials, Trace)
                                            : ahead > 0 ? _git.Push(path, credentials, Trace)
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

    [RelayCommand]
    private void ShowSettings()
    {
        RefreshHostEntries();
        IsSettingsPageVisible = true;
    }

    [RelayCommand]
    private void ShowRepository() => IsSettingsPageVisible = false;

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

        var (info, branches, changes, history) = await Task.Run(() => (
            _git.OpenRepository(path),
            _git.GetBranches(path),
            _git.GetWorkingChanges(path),
            _git.GetHistory(path, HistoryLimit)));

        Ahead = info.Ahead;
        Behind = info.Behind;
        HasUpstream = info.HasUpstream;
        LastFetched = info.LastFetched;

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
