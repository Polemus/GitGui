using System.Net;
using System.Text.Json;
using Omnigit.HostProviders;
using Omnigit.Services;
using Omnigit.ViewModels;

namespace Omnigit.Tests;

/// <summary>
/// Listing pull requests, the links that open them, and the ref a checkout fetches.
/// All three come out of the manifest, so a site added from the UI gets them too -
/// which is only true while the settings form carries the fields as well as the file.
/// </summary>
public class PullRequestTests
{
    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };

    // ---- Reading a site's answer -------------------------------------------

    /// <summary>Gitea's shape, which is GitHub's shape.</summary>
    private const string GiteaPulls = """
        [
          {
            "number": 12,
            "title": "Speed up the diff parser",
            "user": { "login": "contributor" },
            "head": { "ref": "faster-diffs" },
            "base": { "ref": "main" },
            "draft": false,
            "updated_at": "2026-08-01T10:00:00Z",
            "html_url": "https://git.example.com/polemus/omnigit/pulls/12"
          },
          {
            "number": 13,
            "title": "Work in progress",
            "user": { "login": "someone" },
            "head": { "ref": "wip" },
            "base": { "ref": "main" },
            "draft": true,
            "updated_at": "2026-07-30T10:00:00Z",
            "html_url": "https://git.example.com/polemus/omnigit/pulls/13"
          }
        ]
        """;

    private static ManifestHostProvider ProviderFor(HostManifest manifest, string path, string body)
        => new(manifest, new HttpClient(new StubHandler(path, body)));

    private static HostManifest GiteaManifest() => JsonSerializer.Deserialize<HostManifest>(
        File.ReadAllText(ManifestPath("gitea.json")), Read)!;

    private static HostManifest GitLabManifest() => JsonSerializer.Deserialize<HostManifest>(
        File.ReadAllText(ManifestPath("gitlab.json")), Read)!;

    /// <summary>The manifests we ship, read from source rather than copied into the test.</summary>
    private static string ManifestPath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "Omnigit", "HostProviders", "Manifests", name);
    }

    private static HostAccount Account(string baseUrl) => new()
    {
        ProviderId = "gitea",
        BaseUrl = new Uri(baseUrl),
        Login = "tester",
        DisplayName = "Tester",
        Token = "t0ken",
    };

    [Fact]
    public async Task ReadsAPullRequestListThroughTheManifestMapping()
    {
        var provider = ProviderFor(
            GiteaManifest(), "/api/v1/repos/polemus/omnigit/pulls", GiteaPulls);

        var pulls = await provider.ListPullRequestsAsync(
            Account("https://git.example.com"), "polemus", "omnigit", default);

        Assert.Equal(2, pulls.Count);

        var first = pulls[0];
        Assert.Equal(12, first.Number);
        Assert.Equal("Speed up the diff parser", first.Title);
        Assert.Equal("contributor", first.Author);
        Assert.Equal("faster-diffs", first.SourceBranch);
        Assert.Equal("main", first.TargetBranch);
        Assert.False(first.IsDraft);
        Assert.Equal("https://git.example.com/polemus/omnigit/pulls/12", first.WebUrl);

        // The checkout branch is never the source branch's own name - a fork's branch
        // could be called anything, including something already here.
        Assert.Equal("pr/12", first.LocalBranchName);
        Assert.True(pulls[1].IsDraft);
    }

    /// <summary>
    /// GitLab calls it a merge request, numbers it with iid rather than id, and files
    /// the head somewhere else. Every one of those is manifest data, not code.
    /// </summary>
    [Fact]
    public async Task ReadsGitLabsMergeRequestsThroughTheSameCode()
    {
        const string body = """
            [
              {
                "id": 99001,
                "iid": 4,
                "title": "Add the exporter",
                "author": { "username": "someone" },
                "source_branch": "exporter",
                "target_branch": "trunk",
                "draft": false,
                "updated_at": "2026-08-02T09:00:00Z",
                "web_url": "https://gitlab.example.com/group/proj/-/merge_requests/4"
              }
            ]
            """;

        var manifest = GitLabManifest();
        var provider = ProviderFor(manifest, "/api/v4/projects/group%2Fproj/merge_requests", body);

        var pulls = await provider.ListPullRequestsAsync(
            Account("https://gitlab.example.com"), "group", "proj", default);

        var only = Assert.Single(pulls);

        // iid, not id: id is global and would name someone else's merge request.
        Assert.Equal(4, only.Number);
        Assert.Equal("exporter", only.SourceBranch);
        Assert.Equal("trunk", only.TargetBranch);

        Assert.Equal("refs/merge-requests/4/head", WebLinks.PullRequestRef(4, manifest.PullRequestRef));
    }

    [Fact]
    public async Task AnEntryWithNoNumberIsSkippedRatherThanFailingTheList()
    {
        const string body = """
            [
              { "title": "Nothing to fetch and nothing to open" },
              { "number": 7, "title": "Fine", "head": { "ref": "a" }, "base": { "ref": "b" } }
            ]
            """;

        var provider = ProviderFor(GiteaManifest(), "/api/v1/repos/o/r/pulls", body);

        var pulls = await provider.ListPullRequestsAsync(Account("https://git.example.com"), "o", "r", default);

        Assert.Equal(7, Assert.Single(pulls).Number);
    }

    [Fact]
    public async Task AHostWithNoPullRequestEndpointListsNothingAndSaysSo()
    {
        var manifest = GiteaManifest();
        manifest.Endpoints.PullRequests = string.Empty;

        var provider = ProviderFor(manifest, "/unused", "[]");

        Assert.False(provider.Capabilities.CanListPullRequests);
        Assert.Empty(await provider.ListPullRequestsAsync(Account("https://git.example.com"), "o", "r", default));
    }

    // ---- The links ---------------------------------------------------------

    [Fact]
    public void TheComparePageIsWhereANewPullRequestStarts()
    {
        var url = WebLinks.NewPullRequestUrl(
            new Uri("https://github.com"), "polemus", "omnigit", "faster-diffs", "main");

        Assert.Equal(
            "https://github.com/polemus/omnigit/compare/main...faster-diffs?expand=1",
            url?.ToString());
    }

    /// <summary>A branch name is not URL-safe: a "#" in one would cut the link short.</summary>
    [Fact]
    public void BranchNamesAreEscapedIntoTheLink()
    {
        var url = WebLinks.NewPullRequestUrl(
            new Uri("https://github.com"), "polemus", "omnigit", "fix/#12", "main");

        Assert.Contains("fix%2F%2312", url?.ToString());
        Assert.DoesNotContain("#12", url?.ToString());
    }

    [Fact]
    public void GitLabsFormIsADifferentUrlShapeEntirely()
    {
        var url = WebLinks.NewPullRequestUrl(
            new Uri("https://gitlab.example.com"), "group", "proj", "exporter", "trunk",
            GitLabManifest().WebUrls.NewPullRequest);

        Assert.NotNull(url);
        Assert.Contains("/-/merge_requests/new", url!.ToString());
        Assert.Contains("source_branch%5D=exporter", url.ToString());
    }

    [Fact]
    public void APullRequestRefIsNumberedFromTheTemplate()
    {
        Assert.Equal("refs/pull/12/head", WebLinks.PullRequestRef(12));
        Assert.Equal("refs/pull/12/head", WebLinks.PullRequestRef(12, "  "));
    }

    // ---- The settings form -------------------------------------------------

    /// <summary>
    /// A host added from the UI is written back through the form. Anything the form
    /// forgets is dropped from the file the moment someone edits the host - which is
    /// how a working site quietly loses its pull requests.
    /// </summary>
    [Fact]
    public void EditingAHostKeepsItsPullRequestSettings()
    {
        var original = HostDraftViewModel.GitLabLike();
        original.Id = "gitlab";
        original.DisplayName = "GitLab";

        var reopened = HostDraftViewModel.FromManifest(original.ToManifest());

        Assert.Equal(original.PullRequestsEndpoint, reopened.PullRequestsEndpoint);
        Assert.Equal("iid", reopened.PrNumberField);
        Assert.Equal("source_branch", reopened.PrSourceBranchField);
        Assert.Equal("web_url", reopened.PrWebUrlField);
        Assert.Equal("refs/merge-requests/{number}/head", reopened.PullRequestRef);
        Assert.Contains("/-/merge_requests/new", reopened.NewPullRequestUrlTemplate);
    }

    [Fact]
    public void AHostThatSaysNothingAboutPullRequestsStillGetsTheUsualShape()
    {
        var manifest = JsonSerializer.Deserialize<HostManifest>(
            """{ "id": "plain", "displayName": "Plain" }""", Read)!;

        Assert.Equal("refs/pull/{number}/head", manifest.PullRequestRef);
        Assert.Equal("{base}/{owner}/{repo}/compare/{target}...{source}?expand=1",
            manifest.WebUrls.NewPullRequest);

        // ...but it can't list them, so the picker won't offer the tab.
        Assert.Empty(manifest.Endpoints.PullRequests);
    }

    private sealed class StubHandler(string path, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // AbsolutePath decodes %2F, which is exactly what GitLab's project id relies
            // on staying encoded - so the raw string is what gets compared.
            var asked = request.RequestUri!.GetComponents(
                UriComponents.Path, UriFormat.UriEscaped);

            var response = string.Equals("/" + asked, path, StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"no route for /{asked}"),
                };

            return Task.FromResult(response);
        }
    }
}
