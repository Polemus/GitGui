using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GitGui.HostProviders;

/// <summary>How one check in a connection test turned out.</summary>
public enum ProbeOutcome
{
    Passed,
    Failed,

    /// <summary>Nothing to check - no recognise rule, or no token to check it with.</summary>
    Skipped,
}

/// <summary>One check and what it found. <paramref name="Detail"/> is shown as-is.</summary>
public sealed record ProbeStep(string Name, ProbeOutcome Outcome, string Detail);

/// <summary>The result of testing a manifest against a real server.</summary>
public sealed record HostConnectionReport(IReadOnlyList<ProbeStep> Steps)
{
    public bool Passed
    {
        get
        {
            foreach (var step in Steps)
            {
                if (step.Outcome == ProbeOutcome.Failed)
                    return false;
            }

            return true;
        }
    }
}

/// <summary>
/// Runs a draft manifest against a real server and says what happened at each step.
/// </summary>
/// <remarks>
/// The point is the detail. <see cref="IHostProvider.RecognisesAsync"/> answers only
/// yes or no, which is right when GitGui is picking a provider but useless when the
/// question is "why doesn't my site work" - so recognition is repeated here with the
/// status code and the response kept. Sign-in and the repository list go through
/// <see cref="ManifestHostProvider"/> itself, so the test exercises the same code the
/// app will, not a lookalike.
/// </remarks>
public sealed class HostConnectionTester(HttpClient http)
{
    /// <summary>
    /// A test is something the user is waiting on, so it gives up long before
    /// <see cref="HttpClient"/>'s default would. A server this slow is a failed test.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Accepts what people actually type. A bare host is assumed to be https, because
    /// http would be the surprising choice for something holding a token.
    /// </summary>
    public static bool TryParseBaseUrl(string? text, out Uri baseUrl)
    {
        baseUrl = null!;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();

        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;

        return Uri.TryCreate(trimmed, UriKind.Absolute, out baseUrl!)
               && baseUrl.Scheme is "http" or "https"
               && !string.IsNullOrEmpty(baseUrl.Host);
    }

    /// <summary>
    /// Checks as much as the given token allows: without one, only recognition can be
    /// tested, which is still the check that catches a wrong base path.
    /// </summary>
    public async Task<HostConnectionReport> RunAsync(
        HostManifest manifest, Uri baseUrl, string? token, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var steps = new List<ProbeStep> { await RecogniseAsync(manifest, baseUrl, timeout.Token) };

        var provider = new ManifestHostProvider(manifest, http);
        var (signIn, account) = await SignInAsync(provider, manifest, baseUrl, token, timeout.Token);
        steps.Add(signIn);
        steps.Add(await RepositoriesAsync(provider, manifest, account, timeout.Token));

        return new HostConnectionReport(steps);
    }

    private async Task<ProbeStep> RecogniseAsync(
        HostManifest manifest, Uri baseUrl, CancellationToken cancellationToken)
    {
        const string name = "Recognising the site";

        if (manifest.Recognise is not { } rule || string.IsNullOrWhiteSpace(rule.Path))
        {
            return new ProbeStep(name, ProbeOutcome.Skipped,
                "No recognise path, so GitGui can't tell this site apart from any other. "
                + "Repositories cloned from it won't be grouped under it.");
        }

        var url = Combine(baseUrl, rule.Path);

        try
        {
            using var response = await http.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new ProbeStep(name, ProbeOutcome.Failed,
                    $"{url} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return new ProbeStep(name, ProbeOutcome.Failed,
                    $"{url} answered, but with something that isn't JSON: {Excerpt(body)}");
            }

            using (document)
            {
                if (string.IsNullOrWhiteSpace(rule.ExpectField))
                {
                    return new ProbeStep(name, ProbeOutcome.Passed,
                        $"{url} answered with JSON. No field is checked, so any JSON here counts as a match.");
                }

                if (!document.RootElement.TryGetProperty(rule.ExpectField, out _))
                {
                    return new ProbeStep(name, ProbeOutcome.Failed,
                        $"{url} answered, but has no '{rule.ExpectField}' field: {Excerpt(body)}");
                }

                return new ProbeStep(name, ProbeOutcome.Passed,
                    $"{url} answered with a '{rule.ExpectField}' field.");
            }
        }
        catch (OperationCanceledException)
        {
            return new ProbeStep(name, ProbeOutcome.Failed, $"{url} didn't answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            return new ProbeStep(name, ProbeOutcome.Failed, $"Could not reach {url}: {ex.Message}");
        }
    }

    private static async Task<(ProbeStep Step, HostAccount? Account)> SignInAsync(
        ManifestHostProvider provider,
        HostManifest manifest,
        Uri baseUrl,
        string? token,
        CancellationToken cancellationToken)
    {
        const string name = "Signing in";

        if (string.IsNullOrWhiteSpace(manifest.Endpoints.CurrentUser))
            return (new ProbeStep(name, ProbeOutcome.Failed, "No current-user endpoint, so sign-in cannot work."), null);

        if (string.IsNullOrWhiteSpace(token))
        {
            return (new ProbeStep(name, ProbeOutcome.Skipped,
                "Paste a token above to check the sign-in endpoint and the field names."), null);
        }

        try
        {
            var account = await provider.SignInWithTokenAsync(baseUrl, token.Trim(), cancellationToken);

            return (new ProbeStep(name, ProbeOutcome.Passed,
                $"Signed in as {account.Login} ({account.DisplayName})."), account);
        }
        catch (OperationCanceledException)
        {
            return (new ProbeStep(name, ProbeOutcome.Failed, "The sign-in request timed out."), null);
        }
        catch (HostProviderException ex)
        {
            return (new ProbeStep(name, ProbeOutcome.Failed, ex.Message), null);
        }
    }

    private static async Task<ProbeStep> RepositoriesAsync(
        ManifestHostProvider provider,
        HostManifest manifest,
        HostAccount? account,
        CancellationToken cancellationToken)
    {
        const string name = "Listing repositories";

        if (string.IsNullOrWhiteSpace(manifest.Endpoints.Repositories))
        {
            return new ProbeStep(name, ProbeOutcome.Skipped,
                "No repositories endpoint. Sign-in will still work; browsing and cloning won't.");
        }

        if (account is null)
            return new ProbeStep(name, ProbeOutcome.Skipped, "Needs a working sign-in first.");

        try
        {
            var repositories = await provider.ListRepositoriesAsync(account, cancellationToken);

            if (repositories.Count == 0)
            {
                // An empty list is ambiguous: it means either the account really has no
                // repositories, or the name/clone-url mappings dropped every one of them.
                return new ProbeStep(name, ProbeOutcome.Passed,
                    "The endpoint answered, but listed no repositories. If the account has some, "
                    + "check the name and clone URL mappings.");
            }

            var first = repositories[0];

            return new ProbeStep(name, ProbeOutcome.Passed,
                $"Found {repositories.Count} repositor{(repositories.Count == 1 ? "y" : "ies")}, "
                + $"e.g. {first.Owner}/{first.Name} at {first.CloneUrl}.");
        }
        catch (OperationCanceledException)
        {
            return new ProbeStep(name, ProbeOutcome.Failed, "The repository request timed out.");
        }
        catch (HostProviderException ex)
        {
            return new ProbeStep(name, ProbeOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Enough of a response to recognise, not enough to fill the panel.</summary>
    private static string Excerpt(string body)
    {
        var flat = body.Replace('\n', ' ').Replace('\r', ' ').Trim();

        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }

    /// <summary>Matches <see cref="ManifestHostProvider"/>: a path prefix on the base is kept.</summary>
    private static Uri Combine(Uri baseUrl, string path)
    {
        var left = baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri($"{left}/{path.TrimStart('/')}");
    }
}
