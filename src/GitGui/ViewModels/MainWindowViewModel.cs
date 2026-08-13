using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly bool _isDesignTime;

    /// <summary>Design-time constructor. Fills the previewer from sample data only.</summary>
    public MainWindowViewModel()
        : this(new GitService(), new RepositoryStore(), new FolderPicker(), designTime: true)
    {
        LoadDesignTimeData();
    }

    public MainWindowViewModel(IGitService git, IRepositoryStore store, IFolderPicker picker)
        : this(git, store, picker, designTime: false)
    {
    }

    private MainWindowViewModel(IGitService git, IRepositoryStore store, IFolderPicker picker, bool designTime)
    {
        _git = git;
        _store = store;
        _picker = picker;
        _isDesignTime = designTime;
    }

    public ObservableCollection<RepositoryInfo> Repositories { get; } = [];
    public ObservableCollection<HostGroupViewModel> RepositoryGroups { get; } = [];
    public ObservableCollection<BranchInfo> Branches { get; } = [];
    public ObservableCollection<CommitInfo> History { get; } = [];
    public ObservableCollection<FileChangeViewModel> Changes { get; } = [];
    public ObservableCollection<FileChange> SelectedCommitFiles { get; } = [];

    // Accounts stay sample-only until host APIs land.
    public ObservableCollection<Account> Accounts { get; } = new(MockData.Accounts);
    public ObservableCollection<GitHost> Hosts { get; } = [];

    // ---- Loading / errors --------------------------------------------------

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? StatusMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);
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

    [ObservableProperty]
    public partial bool IsAccountsPageVisible { get; set; }

    // ---- Live repository status -------------------------------------------
    // Held here rather than on RepositoryInfo, which stays immutable identity.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncActionLabel))]
    [NotifyPropertyChangedFor(nameof(SyncCountLabel))]
    [NotifyPropertyChangedFor(nameof(HasSyncCount))]
    public partial int Ahead { get; set; }

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

    public bool CanCommit => StagedCount > 0
                             && !string.IsNullOrWhiteSpace(CommitSummary)
                             && !IsBusy;

    public string CommitButtonLabel => $"Commit to {SelectedBranch?.Name ?? "branch"}";

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

    // ---- Startup -----------------------------------------------------------

    /// <summary>Loads the remembered repositories. Called once after the window opens.</summary>
    public async Task InitialiseAsync()
    {
        if (_isDesignTime)
            return;

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
            ErrorMessage = $"'{path}' is not a git repository.";
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
            SelectedRepository = null;
            Branches.Clear();
            Changes.Clear();
            History.Clear();
            SelectedCommitFiles.Clear();
        }

        if (Repositories.Count > 0)
            await OpenRepositoryAsync(Repositories[0]);
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
            StatusMessage = $"Switched to {branch.Name}";
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

        var committed = false;

        await RunAsync(async () =>
        {
            var sha = await Task.Run(() => _git.Commit(path, paths, summary, description));
            StatusMessage = $"Committed {sha[..7]}";
            committed = true;
        });

        if (!committed)
            return;

        CommitSummary = string.Empty;
        CommitDescription = string.Empty;
        await OpenRepositoryAsync(repo);
    }

    [RelayCommand]
    private void Sync()
        => StatusMessage = "Fetch, push and pull arrive with account support — "
                         + "they need credentials for the host.";

    [RelayCommand]
    private void ShowChangesTab() => SelectedTabIndex = 0;

    [RelayCommand]
    private void ShowHistoryTab() => SelectedTabIndex = 1;

    [RelayCommand]
    private void ShowAccounts() => IsAccountsPageVisible = true;

    [RelayCommand]
    private void ShowRepository() => IsAccountsPageVisible = false;

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    [RelayCommand]
    private void DismissStatus() => StatusMessage = null;

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
            ErrorMessage = $"Could not open '{path}': {ex.Message}";
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

        await RunAsync(async () =>
        {
            var path = repository.LocalPath;

            var (info, branches, changes, history) = await Task.Run(() => (
                _git.OpenRepository(path),
                _git.GetBranches(path),
                _git.GetWorkingChanges(path),
                _git.GetHistory(path, HistoryLimit)));

            Ahead = info.Ahead;
            Behind = info.Behind;
            LastFetched = info.LastFetched;

            Replace(Branches, branches);
            Replace(History, history);

            foreach (var change in Changes)
                change.PropertyChanged -= OnChangePropertyChanged;

            Changes.Clear();
            foreach (var change in changes)
            {
                var vm = new FileChangeViewModel(change);
                vm.PropertyChanged += OnChangePropertyChanged;
                Changes.Add(vm);
            }

            SelectedBranch = Branches.FirstOrDefault(b => b.IsCurrent) ?? Branches.FirstOrDefault();
            SelectedChange = Changes.FirstOrDefault();
            SelectedCommit = History.FirstOrDefault();

            NotifyChangeCountsChanged();
        });
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
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Runs a git operation with busy state and error capture around it.</summary>
    private async Task RunAsync(Func<Task> operation)
    {
        IsBusy = true;
        ErrorMessage = null;
        CommitCommand.NotifyCanExecuteChanged();

        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
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
