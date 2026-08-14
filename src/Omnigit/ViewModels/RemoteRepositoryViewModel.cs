using Omnigit.HostProviders;

namespace Omnigit.ViewModels;

/// <summary>One row in the browse-and-clone list.</summary>
public sealed class RemoteRepositoryViewModel(RemoteRepository model, HostAccount account, bool isCloned)
{
    public RemoteRepository Model { get; } = model;

    /// <summary>Which signed-in account listed this, and so which token clones it.</summary>
    public HostAccount Account { get; } = account;

    public string Name => Model.Name;
    public string FullName => Model.FullName;
    public string CloneUrl => Model.CloneUrl;

    public string Description => string.IsNullOrWhiteSpace(Model.Description)
        ? "No description"
        : Model.Description!;

    public bool IsPrivate => Model.IsPrivate;

    public string VisibilityLabel => Model.IsPrivate ? "Private" : "Public";

    public string UpdatedLabel => Model.UpdatedAt is { } when
        ? Models.TimeFormat.Relative(when)
        : string.Empty;

    /// <summary>Already on disk, so the row offers to open it rather than clone it again.</summary>
    public bool IsCloned { get; } = isCloned;

    public bool CanClone => !IsCloned;
}
