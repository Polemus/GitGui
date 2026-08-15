using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Omnigit.HostProviders;
using Omnigit.Services;

namespace Omnigit.ViewModels;

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

    /// <summary>
    /// Open pull requests for one repository. Empty is a fair answer - the branch picker
    /// simply won't offer the tab for this site.
    /// </summary>
    [ObservableProperty]
    public partial string PullRequestsEndpoint { get; set; } =
        "/api/v1/repos/{owner}/{repo}/pulls?state=open&sort=recentupdate&limit=50";

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

    [ObservableProperty] public partial string PrNumberField { get; set; } = "number";
    [ObservableProperty] public partial string PrTitleField { get; set; } = "title";
    [ObservableProperty] public partial string PrAuthorField { get; set; } = "user.login";
    [ObservableProperty] public partial string PrSourceBranchField { get; set; } = "head.ref";
    [ObservableProperty] public partial string PrTargetBranchField { get; set; } = "base.ref";
    [ObservableProperty] public partial string PrDraftField { get; set; } = "draft";
    [ObservableProperty] public partial string PrUpdatedAtField { get; set; } = "updated_at";
    [ObservableProperty] public partial string PrWebUrlField { get; set; } = "html_url";

    // ---- What libgit2 gets for HTTPS ---------------------------------------

    [ObservableProperty] public partial string GitUsername { get; set; } = "{login}";
    [ObservableProperty] public partial string GitPassword { get; set; } = "{token}";

    // ---- Links into the site's own website ----------------------------------

    /// <summary>
    /// Where "View on …" sends the browser. <c>{base}</c>, <c>{owner}</c>,
    /// <c>{repo}</c> and <c>{sha}</c> are filled in from the clone's remote.
    /// </summary>
    [ObservableProperty]
    public partial string CommitUrlTemplate { get; set; } = WebLinks.DefaultCommitTemplate;

    /// <summary>
    /// The site's own "open a pull request" form. <c>{source}</c> and <c>{target}</c>
    /// are the branches being proposed and merged into.
    /// </summary>
    [ObservableProperty]
    public partial string NewPullRequestUrlTemplate { get; set; } = WebLinks.DefaultNewPullRequestTemplate;

    /// <summary>Where the site keeps pull request heads, over <c>{number}</c>.</summary>
    [ObservableProperty]
    public partial string PullRequestRef { get; set; } = WebLinks.DefaultPullRequestRefSpec;

    /// <summary>True when editing an existing host rather than adding one.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    /// <summary>An id and a user endpoint are the minimum for sign-in to work at all.</summary>
    public bool CanSave => !string.IsNullOrWhiteSpace(Id)
                           && !string.IsNullOrWhiteSpace(DisplayName)
                           && !string.IsNullOrWhiteSpace(CurrentUserEndpoint);

    public string Title => IsEditing ? $"Editing {DisplayName}" : "Add a hosting site";

    // ---- Testing it against a real server -----------------------------------
    // A manifest is only right about a server that exists, so the form carries the
    // URL to try it against. Neither of these two ends up in the manifest.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    public partial string TestUrl { get; set; } = string.Empty;

    /// <summary>Held only for the length of the test. Never written to the manifest.</summary>
    [ObservableProperty]
    public partial string TestToken { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    public partial bool IsTesting { get; set; }

    public ObservableCollection<ProbeStepViewModel> TestSteps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial string? TestSummary { get; set; }

    [ObservableProperty]
    public partial bool TestPassed { get; set; }

    public bool HasTestResult => TestSummary is not null;

    /// <summary>A URL is all that is needed; without a token the test just checks less.</summary>
    public bool CanTest => !IsTesting && HostConnectionTester.TryParseBaseUrl(TestUrl, out _);

    public void ShowTestResult(HostConnectionReport report)
    {
        TestSteps.Clear();

        foreach (var step in report.Steps)
            TestSteps.Add(new ProbeStepViewModel(step));

        TestPassed = report.Passed;
        TestSummary = report.Passed
            ? "Everything Omnigit could check works."
            : "Something is wrong — see below.";
    }

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
        CommitUrlTemplate = "{base}/{owner}/{repo}/-/commit/{sha}",
        PullRequestsEndpoint =
            "/api/v4/projects/{owner}%2F{repo}/merge_requests?state=opened&order_by=updated_at&per_page=50",
        PrNumberField = "iid",
        PrAuthorField = "author.username",
        PrSourceBranchField = "source_branch",
        PrTargetBranchField = "target_branch",
        PrWebUrlField = "web_url",
        PullRequestRef = "refs/merge-requests/{number}/head",
        NewPullRequestUrlTemplate =
            "{base}/{owner}/{repo}/-/merge_requests/new"
            + "?merge_request%5Bsource_branch%5D={source}&merge_request%5Btarget_branch%5D={target}",
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
        PullRequestsEndpoint = manifest.Endpoints.PullRequests,
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
        PrNumberField = manifest.PullRequestFields.Number.Path,
        PrTitleField = manifest.PullRequestFields.Title.Path,
        PrAuthorField = manifest.PullRequestFields.Author.Path,
        PrSourceBranchField = manifest.PullRequestFields.SourceBranch.Path,
        PrTargetBranchField = manifest.PullRequestFields.TargetBranch.Path,
        PrDraftField = manifest.PullRequestFields.IsDraft.Path,
        PrUpdatedAtField = manifest.PullRequestFields.UpdatedAt.Path,
        PrWebUrlField = manifest.PullRequestFields.WebUrl.Path,
        PullRequestRef = manifest.PullRequestRef,
        GitUsername = manifest.GitCredentials.Username,
        GitPassword = manifest.GitCredentials.Password,
        CommitUrlTemplate = manifest.WebUrls.Commit,
        NewPullRequestUrlTemplate = manifest.WebUrls.NewPullRequest,
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
            PullRequests = PullRequestsEndpoint.Trim(),
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
        PullRequestFields = new PullRequestFieldMap
        {
            Number = new FieldRef(PrNumberField.Trim()),
            Title = new FieldRef(PrTitleField.Trim()),
            Author = new FieldRef(PrAuthorField.Trim()),
            SourceBranch = new FieldRef(PrSourceBranchField.Trim()),
            TargetBranch = new FieldRef(PrTargetBranchField.Trim()),
            IsDraft = new FieldRef(PrDraftField.Trim()),
            UpdatedAt = new FieldRef(PrUpdatedAtField.Trim()),
            WebUrl = new FieldRef(PrWebUrlField.Trim()),
        },
        PullRequestRef = string.IsNullOrWhiteSpace(PullRequestRef)
            ? WebLinks.DefaultPullRequestRefSpec
            : PullRequestRef.Trim(),
        GitCredentials = new CredentialTemplate
        {
            Username = GitUsername.Trim(),
            Password = GitPassword.Trim(),
        },
        WebUrls = new WebUrlTemplates
        {
            // Blanked in the form means "the shape everyone else uses", not "no link".
            Commit = string.IsNullOrWhiteSpace(CommitUrlTemplate)
                ? WebLinks.DefaultCommitTemplate
                : CommitUrlTemplate.Trim(),
            NewPullRequest = string.IsNullOrWhiteSpace(NewPullRequestUrlTemplate)
                ? WebLinks.DefaultNewPullRequestTemplate
                : NewPullRequestUrlTemplate.Trim(),
        },
    };
}
