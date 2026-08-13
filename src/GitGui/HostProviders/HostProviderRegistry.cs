using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GitGui.HostProviders;

/// <summary>
/// Holds every hosting site GitGui knows about, from three sources: providers written
/// in code, manifests shipped with the app, and manifests the user drops into their
/// config directory.
/// </summary>
public sealed class HostProviderRegistry
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly List<IHostProvider> _providers = [];
    private readonly List<string> _warnings = [];

    private HostProviderRegistry() { }

    public IReadOnlyList<IHostProvider> Providers => _providers;

    /// <summary>Problems loading manifests, surfaced in the UI rather than swallowed.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Where a user drops their own site descriptions.</summary>
    public static string UserManifestDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitGui", "hosts");

    public static HostProviderRegistry Create(HttpClient http, string? gitHubClientId = null)
    {
        var registry = new HostProviderRegistry();

        // 1. Code providers. GitHub is here only because its browser login can't be
        //    described as data; everything else about it could have been a manifest.
        registry._providers.Add(new GitHubProvider(
            http,
            gitHubClientId ?? Environment.GetEnvironmentVariable("GITGUI_GITHUB_CLIENT_ID")));

        // 2. Manifests shipped with the app. Gitea goes through exactly the same code
        //    path a user-written manifest does, so the format can't quietly rot.
        registry.LoadBuiltInManifests(http);

        // 3. The user's own. These override anything above, so a broken built-in can
        //    always be replaced without waiting for a release.
        registry.LoadUserManifests(http);

        return registry;
    }

    public IHostProvider? ById(string id)
        => _providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Asks each provider whether it recognises the site at this URL. This is what
    /// replaces guessing from the domain name.
    /// </summary>
    public async Task<IHostProvider?> RecogniseAsync(Uri baseUrl, CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            if (await provider.RecognisesAsync(baseUrl, cancellationToken))
                return provider;
        }

        return null;
    }

    private void LoadBuiltInManifests(HttpClient http)
    {
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains(".Manifests.", StringComparison.Ordinal)
                                 && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                    continue;

                var manifest = JsonSerializer.Deserialize<HostManifest>(stream, ManifestJson);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    _warnings.Add($"Built-in manifest '{name}' is missing an id.");
                    continue;
                }

                if (ById(manifest.Id) is not null)
                    continue; // A code provider already covers this site.

                _providers.Add(new ManifestHostProvider(manifest, http));
            }
            catch (JsonException ex)
            {
                _warnings.Add($"Built-in manifest '{name}' is not valid JSON: {ex.Message}");
            }
        }
    }

    private void LoadUserManifests(HttpClient http)
    {
        if (!Directory.Exists(UserManifestDirectory))
            return;

        string[] files;
        try
        {
            files = Directory.GetFiles(UserManifestDirectory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warnings.Add($"Could not read {UserManifestDirectory}: {ex.Message}");
            return;
        }

        foreach (var file in files)
        {
            try
            {
                using var stream = File.OpenRead(file);
                var manifest = JsonSerializer.Deserialize<HostManifest>(stream, ManifestJson);

                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    _warnings.Add($"{Path.GetFileName(file)} is missing an \"id\".");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(manifest.Endpoints.CurrentUser))
                {
                    _warnings.Add($"{Path.GetFileName(file)} has no endpoints.currentUser, so sign-in cannot work.");
                    continue;
                }

                // The user's file wins, so a shipped manifest can always be replaced.
                if (ById(manifest.Id) is { } existing)
                {
                    _providers.Remove(existing);
                    _warnings.Add($"{Path.GetFileName(file)} overrides the built-in '{manifest.Id}' provider.");
                }

                _providers.Add(new ManifestHostProvider(manifest, http));
            }
            catch (JsonException ex)
            {
                _warnings.Add($"{Path.GetFileName(file)} is not valid JSON: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warnings.Add($"Could not read {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }
}
