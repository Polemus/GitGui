using CommunityToolkit.Mvvm.ComponentModel;
using GitGui.HostProviders;

namespace GitGui.ViewModels;

/// <summary>
/// The "add a host" form. Every field maps to one part of a <see cref="HostManifest"/>,
/// because the file stays the source of truth - this is a friendlier way to write one,
/// not a second place hosts can live.
/// </summary>
public partial class HostDraftViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string Id { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string DisplayName { get; set; } = string.Empty;

    // ---- How to tell it is this kind of site --------------------------------

    [ObservableProperty]
    public partial string RecognisePath { get; set; } = "/api/v1/version";

    [ObservableProperty]
    public partial string RecogniseField { get; set; } = "version";

    // ---- Authentication -----------------------------------------------------

    [ObservableProperty]
    public partial string AuthHeaderName { get; set; } = "Authorization";

    [ObservableProperty]
    public partial string AuthHeaderValue { get; set; } = "token {token}";

    // ---- Endpoints ----------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string CurrentUserEndpoint { get; set; } = "/api/v1/user";

    [ObservableProperty]
    public partial string RepositoriesEndpoint { get; set; } = "/api/v1/user/repos?limit=100";

    // ---- Where the values sit in the site's JSON ----------------------------

    [ObservableProperty] public partial string UserLoginField { get; set; } = "login";
    [ObservableProperty] public partial string UserDisplayNameField { get; set; } = "full_name";
    [ObservableProperty] public partial string UserAvatarField { get; set; } = "avatar_url";

    [ObservableProperty] public partial string RepoNameField { get; set; } = "name";
    [ObservableProperty] public partial string RepoOwnerField { get; set; } = "owner.login";
    [ObservableProperty] public partial string RepoCloneUrlField { get; set; } = "clone_url";
    [ObservableProperty] public partial string RepoDefaultBranchField { get; set; } = "default_branch";
    [ObservableProperty] public partial string RepoPrivateField { get; set; } = "private";

    /// <summary>
    /// Set when the site encodes privacy as a string rather than a bool, so the value has
    /// to be compared: GitLab sends visibility "private". Empty means a plain bool.
    /// </summary>
    [ObservableProperty] public partial string RepoPrivateEquals { get; set; } = string.Empty;

    [ObservableProperty] public partial string RepoDescriptionField { get; set; } = "description";
    [ObservableProperty] public partial string RepoUpdatedAtField { get; set; } = "updated_at";

    // ---- What libgit2 gets for HTTPS ---------------------------------------

    [ObservableProperty] public partial string GitUsername { get; set; } = "{login}";
    [ObservableProperty] public partial string GitPassword { get; set; } = "{token}";

    /// <summary>True when editing an existing host rather than adding one.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    /// <summary>An id and a user endpoint are the minimum for sign-in to work at all.</summary>
    public bool CanSave => !string.IsNullOrWhiteSpace(Id)
                           && !string.IsNullOrWhiteSpace(DisplayName)
                           && !string.IsNullOrWhiteSpace(CurrentUserEndpoint);

    public string Title => IsEditing ? $"Editing {DisplayName}" : "Add a hosting site";

    /// <summary>Starts from Gitea's shape, which most self-hosted forges follow.</summary>
    public static HostDraftViewModel GiteaLike() => new();

    /// <summary>GitLab differs enough that guessing from Gitea's defaults would waste time.</summary>
    public static HostDraftViewModel GitLabLike() => new()
    {
        RecognisePath = "/api/v4/version",
        RecogniseField = "version",
        AuthHeaderName = "Authorization",
        AuthHeaderValue = "Bearer {token}",
        CurrentUserEndpoint = "/api/v4/user",
        RepositoriesEndpoint = "/api/v4/projects?membership=true&per_page=100",
        UserLoginField = "username",
        UserDisplayNameField = "name",
        RepoNameField = "path",
        RepoOwnerField = "namespace.path",
        RepoCloneUrlField = "http_url_to_repo",
        RepoPrivateField = "visibility",
        RepoPrivateEquals = "private",
        RepoUpdatedAtField = "last_activity_at",
    };

    public static HostDraftViewModel FromManifest(HostManifest manifest) => new()
    {
        IsEditing = true,
        Id = manifest.Id,
        DisplayName = manifest.DisplayName,
        RecognisePath = manifest.Recognise?.Path ?? string.Empty,
        RecogniseField = manifest.Recognise?.ExpectField ?? string.Empty,
        AuthHeaderName = manifest.AuthHeader?.Name ?? "Authorization",
        AuthHeaderValue = manifest.AuthHeader?.Value ?? "Bearer {token}",
        CurrentUserEndpoint = manifest.Endpoints.CurrentUser,
        RepositoriesEndpoint = manifest.Endpoints.Repositories,
        UserLoginField = manifest.UserFields.Login.Path,
        UserDisplayNameField = manifest.UserFields.DisplayName.Path,
        UserAvatarField = manifest.UserFields.AvatarUrl.Path,
        RepoNameField = manifest.RepositoryFields.Name.Path,
        RepoOwnerField = manifest.RepositoryFields.Owner.Path,
        RepoCloneUrlField = manifest.RepositoryFields.CloneUrl.Path,
        RepoDefaultBranchField = manifest.RepositoryFields.DefaultBranch.Path,
        RepoPrivateField = manifest.RepositoryFields.IsPrivate.Path,
        RepoPrivateEquals = manifest.RepositoryFields.IsPrivate.MatchValue ?? string.Empty,
        RepoDescriptionField = manifest.RepositoryFields.Description.Path,
        RepoUpdatedAtField = manifest.RepositoryFields.UpdatedAt.Path,
        GitUsername = manifest.GitCredentials.Username,
        GitPassword = manifest.GitCredentials.Password,
    };

    public HostManifest ToManifest() => new()
    {
        Id = Id.Trim(),
        DisplayName = DisplayName.Trim(),
        Auth = ["token"],
        Recognise = string.IsNullOrWhiteSpace(RecognisePath)
            ? null
            : new RecogniseRule { Path = RecognisePath.Trim(), ExpectField = RecogniseField.Trim() },
        AuthHeader = new HeaderTemplate { Name = AuthHeaderName.Trim(), Value = AuthHeaderValue.Trim() },
        Endpoints = new EndpointSet
        {
            CurrentUser = CurrentUserEndpoint.Trim(),
            Repositories = RepositoriesEndpoint.Trim(),
        },
        UserFields = new UserFieldMap
        {
            Login = new FieldRef(UserLoginField.Trim()),
            DisplayName = new FieldRef(UserDisplayNameField.Trim()),
            AvatarUrl = new FieldRef(UserAvatarField.Trim()),
        },
        RepositoryFields = new RepositoryFieldMap
        {
            Name = new FieldRef(RepoNameField.Trim()),
            Owner = new FieldRef(RepoOwnerField.Trim()),
            CloneUrl = new FieldRef(RepoCloneUrlField.Trim()),
            DefaultBranch = new FieldRef(RepoDefaultBranchField.Trim()),
            IsPrivate = new FieldRef(
                RepoPrivateField.Trim(),
                string.IsNullOrWhiteSpace(RepoPrivateEquals) ? null : RepoPrivateEquals.Trim()),
            Description = new FieldRef(RepoDescriptionField.Trim()),
            UpdatedAt = new FieldRef(RepoUpdatedAtField.Trim()),
        },
        GitCredentials = new CredentialTemplate
        {
            Username = GitUsername.Trim(),
            Password = GitPassword.Trim(),
        },
    };
}
