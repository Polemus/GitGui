using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Omnigit.Models;

namespace Omnigit.ViewModels;

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

    // ---- Context menu ------------------------------------------------------
    // The menu offers only what applies to this file, so each entry carries both the
    // pattern it would write and whether it is worth showing at all.

    /// <summary>The path itself, which is what ignoring one file means.</summary>
    public string IgnoreFilePattern => Model.Path;

    /// <summary>Trailing slash, so the pattern matches the directory and not a file of the same name.</summary>
    public string IgnoreFolderPattern => $"{Model.Directory}/";

    public string IgnoreFolderLabel => $"Ignore folder ({Model.Directory}/)";

    /// <summary>Empty for a file with no extension, e.g. LICENSE or Makefile.</summary>
    public string Extension
    {
        get
        {
            var dot = Model.FileName.LastIndexOf('.');
            return dot > 0 && dot < Model.FileName.Length - 1
                ? Model.FileName[(dot + 1)..]
                : string.Empty;
        }
    }

    public bool HasExtension => Extension.Length > 0;

    public string IgnoreExtensionPattern => $"*.{Extension}";

    public string IgnoreExtensionLabel => $"Ignore all .{Extension} files";
}
