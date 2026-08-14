using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Omnigit.HostProviders;

/// <summary>
/// Holds every hosting site Omnigit knows about, from three sources: providers written
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

    private static readonly JsonSerializerOptions WriteJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<IHostProvider> _providers = [];
    private readonly List<string> _warnings = [];

    private HttpClient _http = null!;
    private string? _gitHubClientId;

    private HostProviderRegistry() { }

    public IReadOnlyList<IHostProvider> Providers => _providers;

    /// <summary>Problems loading manifests, surfaced in the UI rather than swallowed.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Where a user drops their own site descriptions.</summary>
    public static string UserManifestDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Omnigit", "hosts");

    public static HostProviderRegistry Create(HttpClient http, string? gitHubClientId = null)
    {
        var registry = new HostProviderRegistry
        {
            _http = http,
            _gitHubClientId = gitHubClientId
                              ?? Environment.GetEnvironmentVariable("OMNIGIT_GITHUB_CLIENT_ID"),
        };

        registry.Reload();
        return registry;
    }

    /// <summary>
    /// Rebuilds the provider list from disk, in place, so callers holding this registry
    /// see a host added through the UI without restarting.
    /// </summary>
    public void Reload()
    {
        _providers.Clear();
        _warnings.Clear();

        // 1. Code providers. GitHub is here only because its browser login can't be
        //    described as data; everything else about it could have been a manifest.
        _providers.Add(new GitHubProvider(_http, _gitHubClientId));

        // 2. Manifests shipped with the app. Gitea goes through exactly the same code
        //    path a user-written manifest does, so the format can't quietly rot.
        LoadBuiltInManifests(_http);

        // 3. The user's own. These override anything above, so a broken built-in can
        //    always be replaced without waiting for a release.
        LoadUserManifests(_http);
    }

    /// <summary>True if this id came from the user's folder, so the UI may edit or delete it.</summary>
    public bool IsUserDefined(string id) => File.Exists(PathFor(id));

    /// <summary>Reads a user manifest back so it can be edited, or null if there isn't one.</summary>
    public HostManifest? LoadUserManifest(string id)
    {
        var file = PathFor(id);
        if (!File.Exists(file))
            return null;

        try
        {
            using var stream = File.OpenRead(file);
            return JsonSerializer.Deserialize<HostManifest>(stream, ManifestJson);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a manifest to the user's folder and reloads. The file is the source of
    /// truth, exactly as if it had been dropped there by hand - the UI is a convenience
    /// over the format, not a second way of storing hosts.
    /// </summary>
    public void SaveUserManifest(HostManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
            throw new ArgumentException("A host needs an id.", nameof(manifest));

        Directory.CreateDirectory(UserManifestDirectory);
        File.WriteAllText(PathFor(manifest.Id), JsonSerializer.Serialize(manifest, WriteJson));

        Reload();
    }

    /// <summary>Deletes a user manifest and reloads. Built-ins are untouched.</summary>
    public void DeleteUserManifest(string id)
    {
        var file = PathFor(id);

        if (File.Exists(file))
            File.Delete(file);

        Reload();
    }

    /// <summary>
    /// One file per id. The id is restricted to characters that are safe in a file name,
    /// so a manifest can never be made to write outside its own folder.
    /// </summary>
    private static string PathFor(string id)
        => Path.Combine(UserManifestDirectory, $"{Sanitise(id)}.json");

    private static string Sanitise(string id)
    {
        var safe = new char[id.Length];

        for (var i = 0; i < id.Length; i++)
            safe[i] = char.IsLetterOrDigit(id[i]) || id[i] is '-' or '_' ? id[i] : '-';

        return new string(safe);
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

    /// <summary>
    /// Runs a manifest against a real server without saving it, so the host editor can
    /// say what is wrong while the form is still open.
    /// </summary>
    public Task<HostConnectionReport> TestAsync(
        HostManifest manifest, Uri baseUrl, string? token, CancellationToken cancellationToken)
        => new HostConnectionTester(_http).RunAsync(manifest, baseUrl, token, cancellationToken);

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
