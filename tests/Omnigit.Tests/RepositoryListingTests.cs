using System.Net;
using Omnigit.HostProviders;

namespace Omnigit.Tests;

/// <summary>
/// Listing repositories has to follow the Link header, because every site caps a page
/// at 100 and an organisation with more than that had the rest silently missing - the
/// list looked complete and the repositories simply were not in it.
/// </summary>
public class RepositoryListingTests
{
    // ---- the header itself -------------------------------------------------

    [Fact]
    public void Next_finds_the_next_link_among_the_others()
    {
        var header = "<https://api.github.com/user/repos?page=2>; rel=\"next\", "
                     + "<https://api.github.com/user/repos?page=9>; rel=\"last\"";

        Assert.Equal("https://api.github.com/user/repos?page=2",
            LinkHeader.Next([header])?.ToString());
    }

    [Fact]
    public void Next_is_null_on_the_last_page()
    {
        var header = "<https://api.github.com/user/repos?page=8>; rel=\"prev\", "
                     + "<https://api.github.com/user/repos?page=1>; rel=\"first\"";

        Assert.Null(LinkHeader.Next([header]));
    }

    [Fact]
    public void Next_survives_a_comma_inside_the_url()
    {
        // GitHub's own affiliation parameter contains two of them, and this link is the
        // one it sends back. Splitting the header on every comma loses the page.
        var header = "<https://api.github.com/user/repos?affiliation=owner,collaborator,"
                     + "organization_member&page=2>; rel=\"next\"";

        Assert.Equal(
            "https://api.github.com/user/repos?affiliation=owner,collaborator,organization_member&page=2",
            LinkHeader.Next([header])?.ToString());
    }

    [Theory]
    [InlineData("<https://x.test/p2>; rel=next")]
    [InlineData("<https://x.test/p2>; rel='next'")]
    [InlineData("<https://x.test/p2>; type=text/html; rel=\"next\"")]
    [InlineData("<https://x.test/p2>;rel=\"next\"")]
    public void Next_accepts_the_spellings_servers_actually_send(string header)
        => Assert.Equal("https://x.test/p2", LinkHeader.Next([header])?.ToString());

    [Fact]
    public void Next_resolves_a_relative_url_against_the_request()
    {
        var header = "</api/v1/user/repos?page=3>; rel=\"next\"";

        Assert.Equal("https://git.example.com/api/v1/user/repos?page=3",
            LinkHeader.Next([header], new Uri("https://git.example.com/api/v1/user/repos"))?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("<https://x.test/p2>; rel=\"nextish\"")]
    public void Next_is_null_when_there_is_nothing_to_follow(string header)
        => Assert.Null(LinkHeader.Next([header]));

    [Theory]
    [InlineData("<file:///etc/passwd>; rel=\"next\"")]
    [InlineData("<ftp://x.test/p2>; rel=\"next\"")]
    public void Next_refuses_to_follow_a_link_anywhere_but_http(string header)
        => Assert.Null(LinkHeader.Next([header], new Uri("https://git.example.com/api")));

    [Fact]
    public void Next_does_not_read_a_relative_link_as_a_local_file()
    {
        // On Unix "/api/v1/user/repos" parses as an absolute file:// URI, so without a
        // scheme check the next page would be fetched off the local disk.
        var header = "</api/v1/user/repos?page=3>; rel=\"next\"";

        Assert.Null(LinkHeader.Next([header]));
    }

    // ---- the providers -----------------------------------------------------

    private static HostManifest GiteaLike() => new()
    {
        Id = "gitea",
        DisplayName = "Gitea",
        AuthHeader = new HeaderTemplate { Name = "Authorization", Value = "token {token}" },
        Endpoints = new EndpointSet
        {
            CurrentUser = "/api/v1/user",
            Repositories = "/api/v1/user/repos?limit=100",
        },
    };

    private static HostAccount Account(string baseUrl) => new()
    {
        ProviderId = "gitea",
        BaseUrl = new Uri(baseUrl),
        Login = "tester",
        DisplayName = "Tester",
        Token = "t",
    };

    private static string Page(params string[] names)
        => "[" + string.Join(",", names.Select(n =>
            "{\"name\":\"" + n + "\",\"clone_url\":\"https://x.test/o/" + n + ".git\""
            + ",\"owner\":{\"login\":\"o\"}}")) + "]";

    [Fact]
    public async Task A_manifest_site_keeps_following_pages_to_the_end()
    {
        var handler = new PagingHandler(new()
        {
            ["/api/v1/user/repos?limit=100"] = (Page("one", "two"), "</api/v1/user/repos?page=2>; rel=\"next\""),
            ["/api/v1/user/repos?page=2"] = (Page("three"), "</api/v1/user/repos?page=3>; rel=\"next\""),
            ["/api/v1/user/repos?page=3"] = (Page("four"), null),
        });

        var provider = new ManifestHostProvider(GiteaLike(), new HttpClient(handler));
        var repositories = await provider.ListRepositoriesAsync(Account("https://git.example.com"), default);

        Assert.Equal(["one", "two", "three", "four"], repositories.Select(r => r.Name));
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task One_page_with_no_link_is_one_request()
    {
        var handler = new PagingHandler(new()
        {
            ["/api/v1/user/repos?limit=100"] = (Page("only"), null),
        });

        var provider = new ManifestHostProvider(GiteaLike(), new HttpClient(handler));
        var repositories = await provider.ListRepositoriesAsync(Account("https://git.example.com"), default);

        Assert.Single(repositories);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_server_that_always_offers_another_page_is_not_followed_forever()
    {
        // Every response points at itself. Without a cap this never returns.
        var handler = new PagingHandler(new()
        {
            ["/api/v1/user/repos?limit=100"] = (Page("loop"), "</api/v1/user/repos?limit=100>; rel=\"next\""),
        });

        var provider = new ManifestHostProvider(GiteaLike(), new HttpClient(handler));
        var repositories = await provider.ListRepositoriesAsync(Account("https://git.example.com"), default);

        Assert.Equal(50, handler.Requests);
        Assert.Equal(50, repositories.Count);
    }

    /// <summary>Answers by path-and-query, with an optional Link header.</summary>
    private sealed class PagingHandler(Dictionary<string, (string Body, string? Link)> routes)
        : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;

            var key = request.RequestUri!.PathAndQuery;
            if (!routes.TryGetValue(key, out var route))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(route.Body, System.Text.Encoding.UTF8, "application/json"),
            };

            if (route.Link is not null)
                response.Headers.TryAddWithoutValidation("Link", route.Link);

            return Task.FromResult(response);
        }
    }
}
