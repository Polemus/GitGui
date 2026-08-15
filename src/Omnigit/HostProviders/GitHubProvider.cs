using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Omnigit.HostProviders;

/// <summary>
/// GitHub, written as code rather than a manifest because its browser sign-in is a
/// multi-step conversation - ask for a code, wait for the user to approve it in a
/// browser, poll until it flips - which cannot be expressed as endpoint descriptions.
/// Everything else about GitHub could have been a manifest.
/// </summary>
public sealed class GitHubProvider(HttpClient http, string? configuredClientId) : IHostProvider
{
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>Scopes needed to list repositories and to push over HTTPS.</summary>
    private const string Scopes = "repo read:org";

    /// <summary>
    /// Omnigit's own OAuth App, registered on github.com. A client id is public by design:
    /// it names the application on the approval screen and authorises nothing by itself,
    /// and the device flow uses no client secret. There is nothing here to keep out of the
    /// repository, and shipping it is what saves every user from registering their own.
    /// </summary>
    private const string DefaultClientId = "Ov23liTTmSX5cD9G8Ywg";

    public string Id => "github";

    public string DisplayName => "GitHub";

    public HostCapabilities Capabilities { get; } = new()
    {
        AuthMethods = [AuthMethod.BrowserDeviceLogin, AuthMethod.PersonalAccessToken],
        CanListPullRequests = true,
    };

    /// <summary>
    /// Whether browser sign-in can run against this server. It needs a client id, and
    /// <see cref="DefaultClientId"/> is registered on github.com, so it means nothing to an
    /// Enterprise install - those need OMNIGIT_GITHUB_CLIENT_ID naming an app on that server.
    /// </summary>
    public bool CanUseBrowserLogin(Uri baseUrl) => ClientIdFor(baseUrl) is not null;

    /// <summary>A configured id wins everywhere; the built-in one applies only to github.com.</summary>
    private string? ClientIdFor(Uri baseUrl)
        => !string.IsNullOrWhiteSpace(configuredClientId) ? configuredClientId
            : IsDotCom(baseUrl) ? DefaultClientId
            : null;

    public async Task<bool> RecognisesAsync(Uri baseUrl, CancellationToken cancellationToken)
    {
        if (IsDotCom(baseUrl))
            return true;

        // GitHub Enterprise answers /api/v3 with a rate-limit document.
        try
        {
            using var request = Request(HttpMethod.Get, new Uri(ApiBase(baseUrl), "rate_limit"), token: null);
            using var response = await http.SendAsync(request, cancellationToken);

            return response.Headers.Contains("X-GitHub-Media-Type")
                   || response.Headers.Contains("x-github-request-id");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<HostAccount> SignInWithTokenAsync(Uri baseUrl, string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new HostProviderException("A token is required.");

        return await FetchAccountAsync(baseUrl, token, cancellationToken);
    }

    public async Task<DeviceLogin> StartBrowserLoginAsync(Uri baseUrl, CancellationToken cancellationToken)
    {
        if (ClientIdFor(baseUrl) is not { } clientId)
        {
            throw new HostProviderException(
                $"Browser sign-in to {baseUrl.Host} needs an OAuth App registered on that server, "
                + "which identifies Omnigit to it and cannot be shared or invented. Register one at "
                + "Settings → Developer settings → OAuth Apps (tick 'Enable Device Flow'), then set "
                + "OMNIGIT_GITHUB_CLIENT_ID. A personal access token works without any of that.");
        }

        using var content = Form(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = Scopes,
        });

        using var document = await PostFormAsync(new Uri(WebBase(baseUrl), "login/device/code"), content, cancellationToken);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var error))
            throw new HostProviderException($"GitHub refused the sign-in request: {error.GetString()}");

        var verification = root.TryGetProperty("verification_uri", out var v) ? v.GetString() : null;

        return new DeviceLogin
        {
            DeviceCode = root.GetProperty("device_code").GetString()!,
            UserCode = root.GetProperty("user_code").GetString()!,
            VerificationUri = new Uri(verification ?? "https://github.com/login/device"),
            IntervalSeconds = root.TryGetProperty("interval", out var i) ? i.GetInt32() : 5,
            ExpiresAt = DateTimeOffset.Now.AddSeconds(
                root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 900),
        };
    }

    public async Task<HostAccount> CompleteBrowserLoginAsync(
        Uri baseUrl, DeviceLogin login, CancellationToken cancellationToken)
    {
        // Start already refused if there were no id for this server, so this cannot be null;
        // asking again keeps the two halves of the flow reading the same value.
        var clientId = ClientIdFor(baseUrl)
            ?? throw new HostProviderException($"Browser sign-in is not configured for {baseUrl.Host}.");

        var delay = TimeSpan.FromSeconds(Math.Max(1, login.IntervalSeconds));

        while (DateTimeOffset.Now < login.ExpiresAt)
        {
            await Task.Delay(delay, cancellationToken);

            using var content = Form(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["device_code"] = login.DeviceCode,
                ["grant_type"] = DeviceGrantType,
            });

            using var document = await PostFormAsync(
                new Uri(WebBase(baseUrl), "login/oauth/access_token"), content, cancellationToken);

            var root = document.RootElement;

            if (root.TryGetProperty("access_token", out var tokenElement)
                && tokenElement.GetString() is { Length: > 0 } token)
            {
                return await FetchAccountAsync(baseUrl, token, cancellationToken);
            }

            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;

            switch (error)
            {
                case "authorization_pending":
                    continue; // The user hasn't finished in the browser yet.

                case "slow_down":
                    // GitHub asks us to back off; it also sends a new interval.
                    var extra = root.TryGetProperty("interval", out var i) ? i.GetInt32() : 5;
                    delay = TimeSpan.FromSeconds(extra + 1);
                    continue;

                case "expired_token":
                    throw new HostProviderException("The sign-in code expired. Start again.");

                case "access_denied":
                    throw new HostProviderException("Sign-in was declined in the browser.");

                default:
                    throw new HostProviderException($"GitHub sign-in failed: {error ?? "unknown error"}");
            }
        }

        throw new HostProviderException("The sign-in code expired. Start again.");
    }

    public async Task<IReadOnlyList<RemoteRepository>> ListRepositoriesAsync(
        HostAccount account, CancellationToken cancellationToken)
    {
        // 100 is the most GitHub will return at once. An organisation with more than
        // that - which is most of them - needs the rest fetching page by page, or the
        // repositories simply are not there to be cloned and nothing says why.
        Uri? url = new(ApiBase(account.BaseUrl),
            "user/repos?per_page=100&sort=updated&affiliation=owner,collaborator,organization_member");

        var repositories = new List<RemoteRepository>();

        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            var (document, next) = await GetJsonPageAsync(url, account.Token, cancellationToken);

            using (document)
            {
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Array)
                    throw new HostProviderException("GitHub returned an unexpected repository list.");

                foreach (var item in root.EnumerateArray())
                {
                    var name = Str(item, "name");
                    var cloneUrl = Str(item, "clone_url");

                    if (name is null || cloneUrl is null)
                        continue;

                    repositories.Add(new RemoteRepository
                    {
                        Name = name,
                        Owner = item.TryGetProperty("owner", out var owner) ? Str(owner, "login") ?? string.Empty : string.Empty,
                        CloneUrl = cloneUrl,
                        DefaultBranch = Str(item, "default_branch") ?? "main",
                        IsPrivate = item.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True,
                        Description = Str(item, "description"),
                        UpdatedAt = DateTimeOffset.TryParse(Str(item, "updated_at"), out var when) ? when : null,
                    });
                }
            }

            url = next;
        }

        return repositories;
    }

    public async Task<IReadOnlyList<PullRequest>> ListPullRequestsAsync(
        HostAccount account, string owner, string repository, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repository))
            return [];

        // One page, most recently touched first: this fills a dropdown, not a report.
        var url = new Uri(ApiBase(account.BaseUrl),
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}"
            + "/pulls?state=open&sort=updated&direction=desc&per_page=50");

        var (document, _) = await GetJsonPageAsync(url, account.Token, cancellationToken);
        var pullRequests = new List<PullRequest>();

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                throw new HostProviderException("GitHub returned an unexpected pull request list.");

            foreach (var item in root.EnumerateArray())
            {
                if (!item.TryGetProperty("number", out var numberElement)
                    || !numberElement.TryGetInt32(out var number))
                {
                    continue;
                }

                pullRequests.Add(new PullRequest
                {
                    Number = number,
                    Title = Str(item, "title") is { Length: > 0 } title ? title : $"#{number}",
                    Author = item.TryGetProperty("user", out var user) ? Str(user, "login") ?? string.Empty : string.Empty,
                    SourceBranch = item.TryGetProperty("head", out var head) ? Str(head, "ref") ?? string.Empty : string.Empty,
                    TargetBranch = item.TryGetProperty("base", out var wanted) ? Str(wanted, "ref") ?? string.Empty : string.Empty,
                    IsDraft = item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
                    UpdatedAt = DateTimeOffset.TryParse(Str(item, "updated_at"), out var when) ? when : null,
                    WebUrl = Str(item, "html_url"),
                });
            }
        }

        return pullRequests;
    }

    /// <summary>
    /// GitHub accepts the token as the password over HTTPS. The username is ignored
    /// but must not be empty.
    /// </summary>
    public GitCredentials GetGitCredentials(HostAccount account) => new(account.Login, account.Token);

    /// <summary>GitHub, and Enterprise with it, uses the shape everything else copied.</summary>
    public string CommitUrlTemplate => Services.WebLinks.DefaultCommitTemplate;

    public string NewPullRequestUrlTemplate => Services.WebLinks.DefaultNewPullRequestTemplate;

    /// <summary>
    /// GitHub keeps every pull request's head on the base repository under
    /// <c>refs/pull/&lt;n&gt;/head</c>, forks included. That is the whole reason a fork's
    /// pull request can be checked out without its remote being added.
    /// </summary>
    public string PullRequestRefSpec => Services.WebLinks.DefaultPullRequestRefSpec;

    // ---------------------------------------------------------------- helpers

    private async Task<HostAccount> FetchAccountAsync(Uri baseUrl, string token, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(new Uri(ApiBase(baseUrl), "user"), token, cancellationToken);
        var root = document.RootElement;

        var login = Str(root, "login")
                    ?? throw new HostProviderException("GitHub did not return an account login.");

        return new HostAccount
        {
            ProviderId = Id,
            BaseUrl = baseUrl,
            Login = login,
            DisplayName = Str(root, "name") is { Length: > 0 } n ? n : login,
            AvatarUrl = Str(root, "avatar_url"),
            Token = token,
        };
    }

    private static bool IsDotCom(Uri baseUrl)
        => baseUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
           || baseUrl.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>github.com has a separate API domain; Enterprise serves it under /api/v3.</summary>
    private static Uri ApiBase(Uri baseUrl)
        => IsDotCom(baseUrl)
            ? new Uri("https://api.github.com/")
            : new Uri($"{baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/')}/api/v3/");

    private static Uri WebBase(Uri baseUrl)
        => new($"{baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/')}/");

    private HttpRequestMessage Request(HttpMethod method, Uri url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        // GitHub rejects API requests without a User-Agent.
        request.Headers.TryAddWithoutValidation("User-Agent", "Omnigit");

        if (!string.IsNullOrEmpty(token))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        return request;
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> values) => new(values);

    private async Task<JsonDocument> PostFormAsync(Uri url, HttpContent content, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, url, token: null);
        request.Content = content;

        // The device endpoints return form-encoded data unless JSON is requested.
        request.Headers.Remove("Accept");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        // The device flow is never paged; only the document matters.
        return (await SendJsonAsync(request, cancellationToken)).Document;
    }

    /// <summary>
    /// A stop on runaway paging. Fifty pages is five thousand repositories, past any
    /// real account, and a server that answered every page with a link to another one
    /// would otherwise be followed until the request was cancelled.
    /// </summary>
    private const int MaxPages = 50;

    private async Task<JsonDocument> GetJsonAsync(Uri url, string token, CancellationToken cancellationToken)
        => (await GetJsonPageAsync(url, token, cancellationToken)).Document;

    private async Task<(JsonDocument Document, Uri? Next)> GetJsonPageAsync(
        Uri url, string token, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, url, token);
        return await SendJsonAsync(request, cancellationToken);
    }

    private async Task<(JsonDocument Document, Uri? Next)> SendJsonAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new HostProviderException($"Could not reach {request.RequestUri?.Host}: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new HostProviderException(
                    "GitHub rejected the token. Check it has not expired and carries the 'repo' scope.");
            }

            // Read before the response is disposed at the end of this block.
            var next = response.Headers.TryGetValues("Link", out var link)
                ? LinkHeader.Next(link, request.RequestUri)
                : null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            try
            {
                return (await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken), next);
            }
            catch (JsonException ex)
            {
                throw new HostProviderException(
                    $"GitHub returned {(int)response.StatusCode} with a body that isn't JSON.", ex);
            }
        }
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
