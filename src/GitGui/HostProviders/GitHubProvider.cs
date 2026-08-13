using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GitGui.HostProviders;

/// <summary>
/// GitHub, written as code rather than a manifest because its browser sign-in is a
/// multi-step conversation - ask for a code, wait for the user to approve it in a
/// browser, poll until it flips - which cannot be expressed as endpoint descriptions.
/// Everything else about GitHub could have been a manifest.
/// </summary>
public sealed class GitHubProvider(HttpClient http, string? clientId) : IHostProvider
{
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>Scopes needed to list repositories and to push over HTTPS.</summary>
    private const string Scopes = "repo read:org";

    public string Id => "github";

    public string DisplayName => "GitHub";

    public HostCapabilities Capabilities { get; } = new()
    {
        AuthMethods = [AuthMethod.BrowserDeviceLogin, AuthMethod.PersonalAccessToken],
    };

    /// <summary>True once an OAuth App client id is configured; browser login needs one.</summary>
    public bool CanUseBrowserLogin => !string.IsNullOrWhiteSpace(clientId);

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
        if (!CanUseBrowserLogin)
        {
            throw new HostProviderException(
                "Browser sign-in needs a GitHub OAuth App client ID, which identifies GitGui to "
                + "GitHub and cannot be shared or invented. Register one at Settings → Developer "
                + "settings → OAuth Apps (tick 'Enable Device Flow'), then set GITGUI_GITHUB_CLIENT_ID. "
                + "A personal access token works without any of that.");
        }

        using var content = Form(new Dictionary<string, string>
        {
            ["client_id"] = clientId!,
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
        var delay = TimeSpan.FromSeconds(Math.Max(1, login.IntervalSeconds));

        while (DateTimeOffset.Now < login.ExpiresAt)
        {
            await Task.Delay(delay, cancellationToken);

            using var content = Form(new Dictionary<string, string>
            {
                ["client_id"] = clientId!,
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
        var url = new Uri(ApiBase(account.BaseUrl), "user/repos?per_page=100&sort=updated&affiliation=owner,collaborator,organization_member");

        using var document = await GetJsonAsync(url, account.Token, cancellationToken);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
            throw new HostProviderException("GitHub returned an unexpected repository list.");

        var repositories = new List<RemoteRepository>();

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

        return repositories;
    }

    /// <summary>
    /// GitHub accepts the token as the password over HTTPS. The username is ignored
    /// but must not be empty.
    /// </summary>
    public GitCredentials GetGitCredentials(HostAccount account) => new(account.Login, account.Token);

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
        request.Headers.TryAddWithoutValidation("User-Agent", "GitGui");

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

        return await SendJsonAsync(request, cancellationToken);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri url, string token, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, url, token);
        return await SendJsonAsync(request, cancellationToken);
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            try
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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
