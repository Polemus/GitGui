using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GitGui.HostProviders;

/// <summary>
/// Turns a <see cref="HostManifest"/> into a working provider. This is what makes a
/// user-written JSON file a first-class hosting site rather than a second-tier one:
/// Gitea itself ships as a manifest and goes through this same code path.
/// </summary>
public sealed class ManifestHostProvider(HostManifest manifest, HttpClient http) : IHostProvider
{
    public string Id => manifest.Id;

    public string DisplayName => manifest.DisplayName;

    public HostCapabilities Capabilities { get; } = new()
    {
        // A manifest can only describe token auth; browser login is a conversation,
        // not a URL, so it needs real code.
        AuthMethods = [AuthMethod.PersonalAccessToken],
        CanListRepositories = !string.IsNullOrEmpty(manifest.Endpoints.Repositories),
    };

    public async Task<bool> RecognisesAsync(Uri baseUrl, CancellationToken cancellationToken)
    {
        if (manifest.Recognise is not { } rule || string.IsNullOrEmpty(rule.Path))
            return false;

        try
        {
            using var response = await http.GetAsync(Combine(baseUrl, rule.Path), cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            return string.IsNullOrEmpty(rule.ExpectField)
                   || document.RootElement.TryGetProperty(rule.ExpectField, out _);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // A site that doesn't answer, or answers with something else, simply isn't
            // this kind of site. Not an error worth surfacing.
            return false;
        }
    }

    public async Task<HostAccount> SignInWithTokenAsync(Uri baseUrl, string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new HostProviderException("A token is required.");

        if (string.IsNullOrEmpty(manifest.Endpoints.CurrentUser))
            throw new HostProviderException($"{DisplayName} has no currentUser endpoint configured.");

        using var document = await GetJsonAsync(Combine(baseUrl, manifest.Endpoints.CurrentUser), token, cancellationToken);
        var root = document.RootElement;

        var login = manifest.UserFields.Login.GetString(root)
                    ?? throw new HostProviderException(
                        $"{DisplayName} did not return a login field at "
                        + $"'{manifest.UserFields.Login.Path}'. Check the manifest's userFields mapping.");

        return new HostAccount
        {
            ProviderId = Id,
            BaseUrl = baseUrl,
            Login = login,
            DisplayName = Blank(manifest.UserFields.DisplayName.GetString(root)) ?? login,
            AvatarUrl = manifest.UserFields.AvatarUrl.GetString(root),
            Token = token,
        };
    }

    public Task<DeviceLogin> StartBrowserLoginAsync(Uri baseUrl, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"{DisplayName} is defined by a manifest, which can only describe token sign-in. "
            + "Create a personal access token on the site and paste it in.");

    public Task<HostAccount> CompleteBrowserLoginAsync(Uri baseUrl, DeviceLogin login, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"{DisplayName} is defined by a manifest, which can only describe token sign-in.");

    public async Task<IReadOnlyList<RemoteRepository>> ListRepositoriesAsync(
        HostAccount account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(manifest.Endpoints.Repositories))
            return [];

        using var document = await GetJsonAsync(
            Combine(account.BaseUrl, manifest.Endpoints.Repositories), account.Token, cancellationToken);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new HostProviderException($"{DisplayName} returned an unexpected repository list.");

        var fields = manifest.RepositoryFields;
        var repositories = new List<RemoteRepository>();

        foreach (var item in root.EnumerateArray())
        {
            var name = fields.Name.GetString(item);
            var cloneUrl = fields.CloneUrl.GetString(item);

            // A repo we can't name or clone is useless; skip rather than fail the lot.
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cloneUrl))
                continue;

            repositories.Add(new RemoteRepository
            {
                Name = name,
                Owner = fields.Owner.GetString(item) ?? string.Empty,
                CloneUrl = cloneUrl,
                DefaultBranch = Blank(fields.DefaultBranch.GetString(item)) ?? "main",
                IsPrivate = fields.IsPrivate.GetBool(item),
                Description = fields.Description.GetString(item),
                UpdatedAt = fields.UpdatedAt.GetDate(item),
            });
        }

        return repositories;
    }

    public GitCredentials GetGitCredentials(HostAccount account) => new(
        Substitute(manifest.GitCredentials.Username, account),
        Substitute(manifest.GitCredentials.Password, account));

    // ---------------------------------------------------------------- helpers

    private async Task<JsonDocument> GetJsonAsync(Uri url, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (manifest.AuthHeader is { } header)
        {
            request.Headers.TryAddWithoutValidation(
                header.Name, header.Value.Replace("{token}", token, StringComparison.Ordinal));
        }

        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // The whole URL, not just the host: when this is wrong it is nearly always
            // the path that is wrong, and the host alone gives nothing to correct.
            throw new HostProviderException($"Could not reach {url}: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new HostProviderException("The token was rejected. Check it has not expired and has the right scopes.");

            if (!response.IsSuccessStatusCode)
                throw new HostProviderException($"{url} returned {(int)response.StatusCode} {response.ReasonPhrase}.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            try
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new HostProviderException($"{url} returned something that isn't JSON.", ex);
            }
        }
    }

    private static string Substitute(string template, HostAccount account) => template
        .Replace("{login}", account.Login, StringComparison.Ordinal)
        .Replace("{token}", account.Token, StringComparison.Ordinal);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Joins a base URL with a manifest path, keeping any path prefix on the base
    /// (a Gitea hosted at example.com/git must keep the /git).
    /// </summary>
    private static Uri Combine(Uri baseUrl, string path)
    {
        var left = baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri($"{left}/{path.TrimStart('/')}");
    }
}
