using System.Text.Json;
using GitGui.HostProviders;
using GitGui.ViewModels;

namespace GitGui.Tests;

/// <summary>
/// The manifest format is the whole extension mechanism, and the settings form now
/// writes it. Both directions are covered: a hand-written file must still load, and
/// what the form produces must come back as what was typed.
/// </summary>
public class HostManifestTests
{
    private static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void LoadsAHandWrittenManifest()
    {
        const string json = """
            {
              "id": "gitea",
              "displayName": "Gitea",
              "recognise": { "path": "/api/v1/version", "expectField": "version" },
              "authHeader": { "name": "Authorization", "value": "token {token}" },
              "endpoints": {
                "currentUser": "/api/v1/user",
                "repositories": "/api/v1/user/repos?limit=100"
              },
              "userFields": { "login": "login", "displayName": "full_name" },
              "repositoryFields": { "cloneUrl": "clone_url", "owner": "owner.login" }
            }
            """;

        var manifest = JsonSerializer.Deserialize<HostManifest>(json, Read)!;

        Assert.Equal("gitea", manifest.Id);
        Assert.Equal("Gitea", manifest.DisplayName);
        Assert.Equal("/api/v1/version", manifest.Recognise!.Path);
        Assert.Equal("token {token}", manifest.AuthHeader!.Value);
        Assert.Equal("/api/v1/user", manifest.Endpoints.CurrentUser);
        Assert.Equal("owner.login", manifest.RepositoryFields.Owner.Path);
    }

    [Fact]
    public void FieldsLeftOutKeepTheirDefaults()
    {
        // A short manifest is legal; the defaults match the shape most forges use.
        var manifest = JsonSerializer.Deserialize<HostManifest>(
            """{ "id": "x", "displayName": "X" }""", Read)!;

        Assert.Equal("login", manifest.UserFields.Login.Path);
        Assert.Equal("clone_url", manifest.RepositoryFields.CloneUrl.Path);
        Assert.Equal("{login}", manifest.GitCredentials.Username);
        Assert.Equal("{token}", manifest.GitCredentials.Password);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreTolerated()
    {
        const string json = """
            {
              // people hand-write these
              "id": "x",
              "displayName": "X",
            }
            """;

        Assert.Equal("x", JsonSerializer.Deserialize<HostManifest>(json, Read)!.Id);
    }

    [Fact]
    public void TheFormProducesAManifestThatSurvivesTheFile()
    {
        var draft = new HostDraftViewModel
        {
            Id = "forgejo",
            DisplayName = "Forgejo",
            RecognisePath = "/api/v1/version",
            RecogniseField = "version",
            AuthHeaderValue = "token {token}",
            CurrentUserEndpoint = "/api/v1/user",
            RepositoriesEndpoint = "/api/v1/user/repos?limit=100",
        };

        var back = JsonSerializer.Deserialize<HostManifest>(
            JsonSerializer.Serialize(draft.ToManifest(), Write), Read)!;

        Assert.Equal("forgejo", back.Id);
        Assert.Equal("Forgejo", back.DisplayName);
        Assert.Equal("/api/v1/version", back.Recognise!.Path);
        Assert.Equal("token {token}", back.AuthHeader!.Value);
        Assert.Equal("/api/v1/user/repos?limit=100", back.Endpoints.Repositories);
    }

    [Fact]
    public void TheGitLabPresetKeepsItsStringValuedPrivacyFlag()
    {
        // The preset exists because GitLab differs; if the object form were lost in
        // the round trip every GitLab repository would look public.
        var manifest = JsonSerializer.Deserialize<HostManifest>(
            JsonSerializer.Serialize(HostDraftViewModel.GitLabLike().ToManifest(), Write), Read)!;

        Assert.Equal("visibility", manifest.RepositoryFields.IsPrivate.Path);
        Assert.Equal("private", manifest.RepositoryFields.IsPrivate.MatchValue);
    }

    [Fact]
    public void AnEmptyPrivacyComparisonStaysAPlainBoolean()
    {
        var draft = new HostDraftViewModel
        {
            Id = "x",
            DisplayName = "X",
            RepoPrivateField = "private",
            RepoPrivateEquals = "   ",
        };

        Assert.Null(draft.ToManifest().RepositoryFields.IsPrivate.MatchValue);
    }

    [Fact]
    public void EditingAnExistingHostRoundTripsThroughTheForm()
    {
        var original = HostDraftViewModel.GitLabLike();
        original.Id = "gitlab";
        original.DisplayName = "GitLab";

        var reopened = HostDraftViewModel.FromManifest(original.ToManifest());

        Assert.True(reopened.IsEditing);
        Assert.Equal("gitlab", reopened.Id);
        Assert.Equal(original.RepositoriesEndpoint, reopened.RepositoriesEndpoint);
        Assert.Equal(original.RepoOwnerField, reopened.RepoOwnerField);
        Assert.Equal("private", reopened.RepoPrivateEquals);

        // GitLab's commit pages sit behind a /-/, so losing this in the round trip
        // would quietly send "View on GitLab" to a 404 after any edit.
        Assert.Equal("{base}/{owner}/{repo}/-/commit/{sha}", reopened.CommitUrlTemplate);
    }

    [Fact]
    public void AHostThatSaysNothingAboutItsWebsiteGetsTheUsualShape()
    {
        var manifest = JsonSerializer.Deserialize<HostManifest>(
            """{ "id": "plain", "displayName": "Plain" }""", Read)!;

        Assert.Equal("{base}/{owner}/{repo}/commit/{sha}", manifest.WebUrls.Commit);
    }

    [Fact]
    public void ClearingTheCommitTemplateFallsBackRatherThanBreakingTheLink()
    {
        var draft = new HostDraftViewModel { Id = "x", DisplayName = "X", CommitUrlTemplate = "  " };

        Assert.Equal("{base}/{owner}/{repo}/commit/{sha}", draft.ToManifest().WebUrls.Commit);
    }

    [Fact]
    public void SavingIsRefusedUntilTheEssentialsAreFilledIn()
    {
        var draft = new HostDraftViewModel();
        Assert.False(draft.CanSave);

        draft.Id = "x";
        Assert.False(draft.CanSave);

        draft.DisplayName = "X";
        Assert.True(draft.CanSave);

        // Without this endpoint there is nothing to sign in against.
        draft.CurrentUserEndpoint = "";
        Assert.False(draft.CanSave);
    }

    [Fact]
    public void WhitespaceIsTrimmedOnTheWayIntoTheFile()
    {
        var draft = new HostDraftViewModel
        {
            Id = "  spaced  ",
            DisplayName = "  Spaced  ",
            CurrentUserEndpoint = "  /api/v1/user  ",
        };

        var manifest = draft.ToManifest();

        Assert.Equal("spaced", manifest.Id);
        Assert.Equal("Spaced", manifest.DisplayName);
        Assert.Equal("/api/v1/user", manifest.Endpoints.CurrentUser);
    }
}
