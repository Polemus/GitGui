using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using GitGui.Models;

namespace GitGui.ViewModels;

/// <summary>
/// A working-tree change plus the one piece of state the list owns: whether the
/// path is checked for inclusion in the next commit.
/// </summary>
public partial class FileChangeViewModel : ViewModelBase
{
    public FileChangeViewModel(FileChange model) => Model = model;

    public FileChange Model { get; }

    [ObservableProperty]
    public partial bool IsStaged { get; set; } = true;

    public string Path => Model.Path;
    public string FileName => Model.FileName;
    public string Directory => Model.Directory;
    public string StatusGlyph => Model.StatusGlyph;
    public int Additions => Model.Additions;
    public int Deletions => Model.Deletions;
    public IReadOnlyList<DiffLine> Diff => Model.Diff;

    public string AdditionsLabel => $"+{Model.Additions}";
    public string DeletionsLabel => $"-{Model.Deletions}";

    public bool IsAdded => Model.IsAdded;
    public bool IsModified => Model.IsModified;
    public bool IsDeleted => Model.IsDeleted;
    public bool IsRenamed => Model.IsRenamed;
    public bool IsConflicted => Model.IsConflicted;

    public bool HasDirectory => !string.IsNullOrEmpty(Model.Directory);
}
