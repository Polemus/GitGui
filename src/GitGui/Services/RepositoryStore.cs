using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitGui.Services;

/// <summary>Remembers which clones the user has added, between launches.</summary>
public interface IRepositoryStore
{
    IReadOnlyList<string> Load();
    void Save(IEnumerable<string> paths);
}

/// <summary>
/// Persists the known-repository list as JSON under the platform's per-user
/// application data directory.
/// </summary>
public sealed class RepositoryStore : IRepositoryStore
{
    private readonly string _file;

    public RepositoryStore()
    {
        // ApplicationData maps to %APPDATA% on Windows, ~/.config on Linux and
        // ~/Library/Application Support on macOS.
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GitGui");

        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "repositories.json");
    }

    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(_file))
                return [];

            var json = File.ReadAllText(_file);
            var state = JsonSerializer.Deserialize(json, StoreJsonContext.Default.StoreState);

            return state?.Repositories?.Where(Directory.Exists).ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable list must never stop the app starting.
            return [];
        }
    }

    public void Save(IEnumerable<string> paths)
    {
        try
        {
            var state = new StoreState { Repositories = paths.Distinct(StringComparer.Ordinal).ToList() };
            var json = JsonSerializer.Serialize(state, StoreJsonContext.Default.StoreState);

            File.WriteAllText(_file, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the list is an annoyance, not a failure worth surfacing.
        }
    }
}

public sealed class StoreState
{
    public List<string> Repositories { get; set; } = [];
}

// Source-generated so the store keeps working if trimming is ever enabled.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(StoreState))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;
