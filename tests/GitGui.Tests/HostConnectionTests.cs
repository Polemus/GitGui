using System.Net;
using GitGui.HostProviders;

namespace GitGui.Tests;

/// <summary>
/// The connection test exists to say *why* a manifest doesn't work, so what is checked
/// here is mostly the detail it reports - a test that only said pass/fail would be no
/// better than the sign-in failure it replaces.
/// </summary>
public class HostConnectionTests
{
    private static HostManifest GiteaLike() => new()
    {
        Id = "gitea",
        DisplayName = "Gitea",
        Recognise = new RecogniseRule { Path = "/api/v1/version", ExpectField = "version" },
        AuthHeader = new HeaderTemplate { Name = "Authorization", Value = "token {token}" },
        Endpoints = new EndpointSet
        {
            CurrentUser = "/api/v1/user",
            Repositories = "/api/v1/user/repos",
        },
    };

    private static HostConnectionTester TesterFor(params (string Path, HttpStatusCode Status, string Body)[] routes)
        => new(new HttpClient(new StubHandler(routes)));

    private static ProbeStep Step(HostConnectionReport report, string startsWith)
        => report.Steps.Single(s => s.Name.StartsWith(startsWith, StringComparison.Ordinal));

    [Theory]
    [InlineData("https://git.example.com", "https://git.example.com/")]
    [InlineData("git.example.com", "https://git.example.com/")]
    [InlineData("  http://localhost:3333  ", "http://localhost:3333/")]
    [InlineData("example.com/git", "https://example.com/git")]
    public void AcceptsTheAddressesPeopleActuallyType(string typed, string expected)
    {
        Assert.True(HostConnectionTester.TryParseBaseUrl(typed, out var url));
        Assert.Equal(expected, url.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://git.example.com")]
    public void RejectsWhatCannotBeProbed(string typed)
        => Assert.False(HostConnectionTester.TryParseBaseUrl(typed, out _));

    [Fact]
    public async Task ReportsTheStatusCodeWhenTheRecognisePathIsWrong()
    {
        var tester = TesterFor(("/api/v1/version", HttpStatusCode.NotFound, ""));

        var report = await tester.RunAsync(GiteaLike(), new Uri("https://git.example.com"), null, default);

        var step = Step(report, "Recognising");
        Assert.False(report.Passed);
        Assert.Equal(ProbeOutcome.Failed, step.Outcome);
        Assert.Contains("404", step.Detail);
        Assert.Contains("/api/v1/version", step.Detail);
    }

    [Fact]
    public async Task NamesTheFieldThatIsMissingFromTheResponse()
    {
        var tester = TesterFor(("/api/v1/version", HttpStatusCode.OK, """{"release":"1.24.0"}"""));

        var report = await tester.RunAsync(GiteaLike(), new Uri("https://git.example.com"), null, default);

        var step = Step(report, "Recognising");
        Assert.Equal(ProbeOutcome.Failed, step.Outcome);
        Assert.Contains("'version'", step.Detail);
        Assert.Contains("release", step.Detail); // the excerpt, so the real name is visible
    }

    [Fact]
    public async Task SaysWhatIsUncheckedWithoutAToken()
    {
        var tester = TesterFor(("/api/v1/version", HttpStatusCode.OK, """{"version":"1.24.0"}"""));

        var report = await tester.RunAsync(GiteaLike(), new Uri("https://git.example.com"), null, default);

        // Nothing failed, so the manifest is not condemned - but two checks didn't run.
        Assert.True(report.Passed);
        Assert.Equal(ProbeOutcome.Passed, Step(report, "Recognising").Outcome);
        Assert.Equal(ProbeOutcome.Skipped, Step(report, "Signing in").Outcome);
        Assert.Equal(ProbeOutcome.Skipped, Step(report, "Listing").Outcome);
    }

    [Fact]
    public async Task ChecksSignInAndTheRepositoryListWhenGivenAToken()
    {
        var tester = TesterFor(
            ("/api/v1/version", HttpStatusCode.OK, """{"version":"1.24.0"}"""),
            ("/api/v1/user", HttpStatusCode.OK, """{"login":"tester","full_name":"A Tester"}"""),
            ("/api/v1/user/repos", HttpStatusCode.OK,
                """[{"name":"gitgui","owner":{"login":"tester"},"clone_url":"https://git.example.com/tester/gitgui.git"}]"""));

        var report = await tester.RunAsync(GiteaLike(), new Uri("https://git.example.com"), "a-token", default);

        Assert.True(report.Passed);
        Assert.Contains("tester", Step(report, "Signing in").Detail);
        Assert.Contains("gitgui", Step(report, "Listing").Detail);
    }

    [Fact]
    public async Task BlamesTheTokenWhenTheUserEndpointRejectsIt()
    {
        var tester = TesterFor(
            ("/api/v1/version", HttpStatusCode.OK, """{"version":"1.24.0"}"""),
            ("/api/v1/user", HttpStatusCode.Unauthorized, ""));

        var report = await tester.RunAsync(GiteaLike(), new Uri("https://git.example.com"), "stale-token", default);

        Assert.False(report.Passed);
        Assert.Contains("token was rejected", Step(report, "Signing in").Detail);

        // Nothing can be said about repositories until sign-in works.
        Assert.Equal(ProbeOutcome.Skipped, Step(report, "Listing").Outcome);
    }

    [Fact]
    public async Task NamesTheEndpointThatAnsweredWithTheWrongStatus()
    {
        // The path is what needs correcting, so naming only the host would be useless -
        // testing a GitLab-shaped manifest against Gitea 404s on every endpoint.
        var tester = TesterFor(("/api/v1/version", HttpStatusCode.OK, """{"version":"1.24.0"}"""));

        var report = await tester.RunAsync(GiteaLike(), new Uri("https://git.example.com"), "a-token", default);

        Assert.Contains("/api/v1/user", Step(report, "Signing in").Detail);
    }

    [Fact]
    public async Task KeepsAPathPrefixOnTheServerAddress()
    {
        var handler = new StubHandler([("/git/api/v1/version", HttpStatusCode.OK, """{"version":"1.24.0"}""")]);

        var report = await new HostConnectionTester(new HttpClient(handler))
            .RunAsync(GiteaLike(), new Uri("https://example.com/git"), null, default);

        Assert.Equal(ProbeOutcome.Passed, Step(report, "Recognising").Outcome);
    }

    [Fact]
    public async Task WarnsWhenTheManifestCannotRecogniseTheSiteAtAll()
    {
        var manifest = GiteaLike();
        manifest.Recognise = null;

        var report = await TesterFor().RunAsync(manifest, new Uri("https://git.example.com"), null, default);

        var step = Step(report, "Recognising");
        Assert.Equal(ProbeOutcome.Skipped, step.Outcome);
        Assert.Contains("grouped", step.Detail);
    }

    /// <summary>Answers the routes it was given and 404s everything else.</summary>
    private sealed class StubHandler((string Path, HttpStatusCode Status, string Body)[] routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var route = routes.FirstOrDefault(r => r.Path == path);

            var response = route.Path is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body) };

            return Task.FromResult(response);
        }
    }
}
