using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitGui.ViewModels;

/// <summary>
/// The question asked when you switch branches with uncommitted work: does it come with
/// you, or stay here. Only exists while the prompt is on screen.
/// </summary>
public partial class BranchSwitchViewModel : ObservableObject
{
    public BranchSwitchViewModel(
        string fromBranch, string targetBranch, bool create, IEnumerable<FileChangeViewModel> changes)
    {
        FromBranch = fromBranch;
        TargetBranch = targetBranch;
        Create = create;

        foreach (var change in changes)
        {
            // Everything comes along unless the user says otherwise, matching what git
            // does when it can.
            var item = new FileChangeViewModel(change.Model) { IsStaged = true };
            item.PropertyChanged += OnFileChanged;
            Files.Add(item);
        }
    }

    public string FromBranch { get; }

    public string TargetBranch { get; }

    /// <summary>True when the target branch doesn't exist yet.</summary>
    public bool Create { get; }

    /// <summary>The uncommitted files. IsStaged means "bring this one across".</summary>
    public ObservableCollection<FileChangeViewModel> Files { get; } = [];

    /// <summary>
    /// True to carry the ticked files, false to leave every change behind. Kept separate
    /// from the ticks so unticking everything and choosing "leave" read the same.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBringing))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(ConfirmLabel))]
    public partial bool LeaveEverything { get; set; }

    public bool IsBringing => !LeaveEverything;

    public int BringCount => Files.Count(f => f.IsStaged);

    public bool AllSelected
    {
        get => Files.Count > 0 && Files.All(f => f.IsStaged);
        set
        {
            foreach (var file in Files)
                file.IsStaged = value;
        }
    }

    public string Title => Create
        ? $"Create {TargetBranch} from {FromBranch}"
        : $"Switch to {TargetBranch}";

    public string Summary
    {
        get
        {
            var total = Files.Count;
            var label = $"{total} uncommitted change{(total == 1 ? "" : "s")}";

            return LeaveEverything
                ? $"{label} will be stashed on {FromBranch}."
                : $"{BringCount} of {label} will come with you; the rest is stashed on {FromBranch}.";
        }
    }

    public string ConfirmLabel => LeaveEverything
        ? "Stash and switch"
        : "Switch";

    /// <summary>Null means "bring everything", which lets the service do a plain checkout.</summary>
    public IReadOnlyList<string>? BringPaths()
    {
        if (LeaveEverything)
            return [];

        return AllSelected ? null : Files.Where(f => f.IsStaged).Select(f => f.Path).ToList();
    }

    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileChangeViewModel.IsStaged))
            return;

        OnPropertyChanged(nameof(BringCount));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(Summary));
    }
}
