using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omnigit.HostProviders;

/// <summary>
/// A description of a git hosting site, written as JSON. This is the whole extension
/// mechanism: drop a file in the hosts folder and Omnigit can talk to a new site with
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

    public PullRequestFieldMap PullRequestFields { get; set; } = new();

    /// <summary>
    /// Where the head of a pull request sits on the origin remote, over
    /// <c>{number}</c>. Fetching this is what lets a pull request from a fork be
    /// checked out without adding the fork as a remote.
    /// </summary>
    public string PullRequestRef { get; set; } = Services.WebLinks.DefaultPullRequestRefSpec;

    /// <summary>What to hand libgit2 for HTTPS push/fetch.</summary>
    public CredentialTemplate GitCredentials { get; set; } = new();

    /// <summary>Pages on the site a user might want to open in a browser.</summary>
    public WebUrlTemplates WebUrls { get; set; } = new();
}

/// <summary>
/// Where things live on the site's own website, as opposed to its API.
/// </summary>
/// <remarks>
/// Templates rather than endpoints, because these are built from what a clone already
/// knows - the remote URL - not fetched. <c>{base}</c>, <c>{owner}</c>, <c>{repo}</c>
/// and <c>{sha}</c> are substituted.
/// </remarks>
public sealed class WebUrlTemplates
{
    public string Commit { get; set; } = Services.WebLinks.DefaultCommitTemplate;

    /// <summary>
    /// The page that opens a pull request, over the same substitutions plus
    /// <c>{source}</c> and <c>{target}</c>.
    /// </summary>
    public string NewPullRequest { get; set; } = Services.WebLinks.DefaultNewPullRequestTemplate;
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

    /// <summary>
    /// Open pull requests for one repository. <c>{owner}</c> and <c>{repo}</c> are
    /// substituted, since unlike the others this endpoint is about a particular clone.
    /// Empty means the site can't list them and the UI hides the tab.
    /// </summary>
    public string PullRequests { get; set; } = string.Empty;
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

/// <summary>
/// Where a pull request's parts sit in the site's JSON. The defaults are GitHub's
/// shape, which Gitea copied; GitLab needs every one of them overridden.
/// </summary>
public sealed class PullRequestFieldMap
{
    public FieldRef Number { get; set; } = new("number");
    public FieldRef Title { get; set; } = new("title");
    public FieldRef Author { get; set; } = new("user.login");
    public FieldRef SourceBranch { get; set; } = new("head.ref");
    public FieldRef TargetBranch { get; set; } = new("base.ref");
    public FieldRef IsDraft { get; set; } = new("draft");
    public FieldRef UpdatedAt { get; set; } = new("updated_at");
    public FieldRef WebUrl { get; set; } = new("html_url");
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

    /// <summary>
    /// Null when the field is missing or isn't a number. A site that sends the number
    /// as a string - some do - still parses, since the string form is read back.
    /// </summary>
    public int? GetInt(JsonElement root)
    {
        if (Resolve(root) is not { } element)
            return null;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
            return number;

        return int.TryParse(GetString(root), out var parsed) ? parsed : null;
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
