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
/// Drives the whole shell. This is a mockup: commands mutate view state so the UI
/// responds like the real thing, but nothing touches a repository on disk.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel()
    {
        Repositories = new ObservableCollection<RepositoryInfo>(MockData.Repositories);
        Branches = new ObservableCollection<BranchInfo>(MockData.Branches);
        Accounts = new ObservableCollection<Account>(MockData.Accounts);
        Hosts = new ObservableCollection<GitHost>(MockData.Hosts);
        History = new ObservableCollection<CommitInfo>(MockData.History);

        Changes = new ObservableCollection<FileChangeViewModel>(
            MockData.WorkingChanges.Select(c => new FileChangeViewModel(c)));

        foreach (var change in Changes)
            change.PropertyChanged += OnChangePropertyChanged;

        RepositoryGroups = new ObservableCollection<HostGroupViewModel>(
            MockData.Hosts.Select(host => new HostGroupViewModel
            {
                Host = host,
                Repositories = MockData.Repositories.Where(r => r.Host == host).ToList(),
            }));

        SelectedRepository = Repositories[0];
        SelectedBranch = Branches[0];
        SelectedChange = Changes[0];
        SelectedCommit = History[0];
    }

    public ObservableCollection<RepositoryInfo> Repositories { get; }
    public ObservableCollection<HostGroupViewModel> RepositoryGroups { get; }
    public ObservableCollection<BranchInfo> Branches { get; }
    public ObservableCollection<Account> Accounts { get; }
    public ObservableCollection<GitHost> Hosts { get; }
    public ObservableCollection<CommitInfo> History { get; }
    public ObservableCollection<FileChangeViewModel> Changes { get; }

    // ---- Selection ---------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncActionLabel))]
    [NotifyPropertyChangedFor(nameof(SyncCountLabel))]
    [NotifyPropertyChangedFor(nameof(HasSyncCount))]
    public partial RepositoryInfo? SelectedRepository { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonLabel))]
    public partial BranchInfo? SelectedBranch { get; set; }

    [ObservableProperty]
    public partial FileChangeViewModel? SelectedChange { get; set; }

    [ObservableProperty]
    public partial CommitInfo? SelectedCommit { get; set; }

    /// <summary>0 = Changes, 1 = History.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChangesTab))]
    [NotifyPropertyChangedFor(nameof(IsHistoryTab))]
    public partial int SelectedTabIndex { get; set; }

    public bool IsChangesTab => SelectedTabIndex == 0;
    public bool IsHistoryTab => SelectedTabIndex == 1;

    [ObservableProperty]
    public partial bool IsAccountsPageVisible { get; set; }

    [ObservableProperty]
    public partial bool IsRepositoryPickerOpen { get; set; }

    [ObservableProperty]
    public partial bool IsBranchPickerOpen { get; set; }

    // ---- Commit box --------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial string CommitSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CommitDescription { get; set; } = string.Empty;

    public int StagedCount => Changes.Count(c => c.IsStaged);

    public string StagedCountLabel =>
        StagedCount == 1 ? "1 changed file" : $"{StagedCount} changed files";

    public bool CanCommit => StagedCount > 0 && !string.IsNullOrWhiteSpace(CommitSummary);

    public string CommitButtonLabel => $"Commit to {SelectedBranch?.Name ?? "main"}";

    public string CommitSummaryPlaceholder => StagedCount == 1
        ? $"Update {Changes.First(c => c.IsStaged).FileName}"
        : "Summary (required)";

    /// <summary>Two-way bound to the select-all checkbox above the file list.</summary>
    public bool AreAllStaged
    {
        get => Changes.Count > 0 && Changes.All(c => c.IsStaged);
        set
        {
            foreach (var change in Changes)
                change.IsStaged = value;
        }
    }

    // ---- Sync bar ----------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncActionLabel))]
    public partial bool IsSyncing { get; set; }

    public string SyncActionLabel
    {
        get
        {
            if (IsSyncing)
                return "Fetching…";
            if (SelectedRepository is { Behind: > 0 })
                return "Pull origin";
            if (SelectedRepository is { Ahead: > 0 })
                return "Push origin";
            return "Fetch origin";
        }
    }

    public string SyncDetailLabel => SelectedRepository?.LastFetchedLabel ?? string.Empty;

    public string SyncCountLabel => SelectedRepository switch
    {
        { Behind: > 0 } r => $"↓ {r.Behind}",
        { Ahead: > 0 } r => $"↑ {r.Ahead}",
        _ => string.Empty,
    };

    public bool HasSyncCount => !string.IsNullOrEmpty(SyncCountLabel);

    // ---- Commands ----------------------------------------------------------

    [RelayCommand]
    private void SelectRepository(RepositoryInfo repository)
    {
        SelectedRepository = repository;
        IsRepositoryPickerOpen = false;
        OnPropertyChanged(nameof(SyncDetailLabel));
    }

    [RelayCommand]
    private void SelectBranch(BranchInfo branch)
    {
        SelectedBranch = branch;
        IsBranchPickerOpen = false;
    }

    [RelayCommand]
    private void ShowChangesTab() => SelectedTabIndex = 0;

    [RelayCommand]
    private void ShowHistoryTab() => SelectedTabIndex = 1;

    [RelayCommand]
    private void ShowAccounts() => IsAccountsPageVisible = true;

    [RelayCommand]
    private void ShowRepository() => IsAccountsPageVisible = false;

    /// <summary>Mock fetch — spins the sync button briefly so the shell feels live.</summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        if (IsSyncing)
            return;

        IsSyncing = true;
        await Task.Delay(1400);
        IsSyncing = false;
        OnPropertyChanged(nameof(SyncDetailLabel));
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void Commit()
    {
        // Mockup: clear the box as though the commit landed.
        CommitSummary = string.Empty;
        CommitDescription = string.Empty;
    }

    private void OnChangePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileChangeViewModel.IsStaged))
            return;

        OnPropertyChanged(nameof(StagedCount));
        OnPropertyChanged(nameof(StagedCountLabel));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(AreAllStaged));
        OnPropertyChanged(nameof(CommitSummaryPlaceholder));
        CommitCommand.NotifyCanExecuteChanged();
    }
}
