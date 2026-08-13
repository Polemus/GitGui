using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitGui.HostProviders;

/// <summary>
/// A description of a git hosting site, written as JSON. This is the whole extension
/// mechanism: drop a file in the hosts folder and GitGui can talk to a new site with
/// no code and no rebuild.
/// </summary>
/// <remarks>
/// Deliberately data, never code. A manifest cannot execute anything, so a site
/// description obtained from someone else cannot read the tokens held for other sites.
/// That is the main reason this is JSON rather than a plugin assembly.
/// </remarks>
public sealed class HostManifest
{
    /// <summary>Stable id, e.g. "gitea". Two manifests with the same id collide.</summary>
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Currently only "token" is meaningful; browser login needs real code.</summary>
    public List<string> Auth { get; set; } = ["token"];

    /// <summary>How to tell that a URL really is this kind of site.</summary>
    public RecogniseRule? Recognise { get; set; }

    /// <summary>The HTTP header carrying the token.</summary>
    public HeaderTemplate? AuthHeader { get; set; }

    public EndpointSet Endpoints { get; set; } = new();

    public UserFieldMap UserFields { get; set; } = new();

    public RepositoryFieldMap RepositoryFields { get; set; } = new();

    /// <summary>What to hand libgit2 for HTTPS push/fetch.</summary>
    public CredentialTemplate GitCredentials { get; set; } = new();
}

/// <summary>Probe a path and check a field exists, e.g. /api/v1/version -> "version".</summary>
public sealed class RecogniseRule
{
    public string Path { get; set; } = string.Empty;
    public string ExpectField { get; set; } = string.Empty;
}

public sealed class HeaderTemplate
{
    public string Name { get; set; } = "Authorization";

    /// <summary><c>{token}</c> is substituted, e.g. "token {token}" or "Bearer {token}".</summary>
    public string Value { get; set; } = "Bearer {token}";
}

public sealed class EndpointSet
{
    public string CurrentUser { get; set; } = string.Empty;
    public string Repositories { get; set; } = string.Empty;
}

public sealed class UserFieldMap
{
    public FieldRef Login { get; set; } = new("login");
    public FieldRef DisplayName { get; set; } = new("full_name");
    public FieldRef AvatarUrl { get; set; } = new("avatar_url");
}

public sealed class RepositoryFieldMap
{
    public FieldRef Name { get; set; } = new("name");
    public FieldRef Owner { get; set; } = new("owner.login");
    public FieldRef CloneUrl { get; set; } = new("clone_url");
    public FieldRef DefaultBranch { get; set; } = new("default_branch");
    public FieldRef IsPrivate { get; set; } = new("private");
    public FieldRef Description { get; set; } = new("description");
    public FieldRef UpdatedAt { get; set; } = new("updated_at");
}

public sealed class CredentialTemplate
{
    /// <summary><c>{login}</c> and <c>{token}</c> are substituted.</summary>
    public string Username { get; set; } = "{login}";

    public string Password { get; set; } = "{token}";
}

/// <summary>
/// Points at a value inside the site's JSON response, by dotted path.
/// </summary>
/// <remarks>
/// Accepts a plain string (<c>"clone_url"</c>, <c>"owner.login"</c>) or, when a site
/// encodes a boolean as a string, an object form: <c>{ "path": "visibility",
/// "equals": "private" }</c>. GitLab needs exactly that for private repositories, so
/// the shorthand alone would not have been enough.
/// </remarks>
[JsonConverter(typeof(FieldRefConverter))]
public sealed class FieldRef(string path, string? matchValue = null)
{
    public string Path { get; } = path;

    /// <summary>
    /// When set, the field's value is compared to this and a bool returned. Named
    /// MatchValue rather than Equals so it doesn't shadow object.Equals; the JSON key
    /// stays "equals".
    /// </summary>
    public string? MatchValue { get; } = matchValue;

    /// <summary>Walks the dotted path and returns the element, or null if absent.</summary>
    public JsonElement? Resolve(JsonElement root)
    {
        if (string.IsNullOrEmpty(Path))
            return null;

        var current = root;

        foreach (var segment in Path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    public string? GetString(JsonElement root)
    {
        if (Resolve(root) is not { } element)
            return null;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.ToString(),
        };
    }

    public bool GetBool(JsonElement root)
    {
        if (Resolve(root) is not { } element)
            return false;

        if (MatchValue is not null)
            return string.Equals(element.GetString(), MatchValue, StringComparison.OrdinalIgnoreCase);

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(element.GetString(), out var b) && b,
            _ => false,
        };
    }

    public DateTimeOffset? GetDate(JsonElement root)
        => DateTimeOffset.TryParse(GetString(root), out var value) ? value : null;
}

/// <summary>Lets a <see cref="FieldRef"/> be written as a bare string or an object.</summary>
public sealed class FieldRefConverter : JsonConverter<FieldRef>
{
    public override FieldRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new FieldRef(reader.GetString() ?? string.Empty);

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A field mapping must be a string or an object.");

        string path = string.Empty;
        string? equals = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var name = reader.GetString();
            reader.Read();

            if (string.Equals(name, "path", StringComparison.OrdinalIgnoreCase))
                path = reader.GetString() ?? string.Empty;
            else if (string.Equals(name, "equals", StringComparison.OrdinalIgnoreCase))
                equals = reader.GetString();
        }

        return new FieldRef(path, equals);
    }

    public override void Write(Utf8JsonWriter writer, FieldRef value, JsonSerializerOptions options)
    {
        if (value.MatchValue is null)
        {
            writer.WriteStringValue(value.Path);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("path", value.Path);
        writer.WriteString("equals", value.MatchValue);
        writer.WriteEndObject();
    }
}
