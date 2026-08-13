using System.Text.Json;
using GitGui.HostProviders;

namespace GitGui.Tests;

/// <summary>
/// FieldRef is how a manifest points at a value inside a site's JSON. Getting it wrong
/// shows up as a silently empty repository list rather than an error, which is exactly
/// the kind of thing that rots unnoticed.
/// </summary>
public class FieldRefTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void ResolvesADottedPath()
    {
        var root = Json("""{ "owner": { "login": "polemus" } }""");

        Assert.Equal("polemus", new FieldRef("owner.login").GetString(root));
    }

    [Fact]
    public void MissingPathsGiveNullRatherThanThrowing()
    {
        var root = Json("""{ "owner": { "login": "polemus" } }""");

        Assert.Null(new FieldRef("owner.missing").GetString(root));
        Assert.Null(new FieldRef("nothing.at.all").GetString(root));
        Assert.Null(new FieldRef(string.Empty).GetString(root));
    }

    [Fact]
    public void WalkingThroughANonObjectStops()
    {
        // "name" is a string, so "name.login" cannot resolve - and must not throw.
        var root = Json("""{ "name": "gitgui" }""");

        Assert.Null(new FieldRef("name.login").GetString(root));
    }

    [Fact]
    public void NonStringValuesComeBackAsText()
    {
        var root = Json("""{ "id": 42 }""");

        Assert.Equal("42", new FieldRef("id").GetString(root));
    }

    [Fact]
    public void JsonNullIsTreatedAsAbsent()
    {
        var root = Json("""{ "description": null }""");

        Assert.Null(new FieldRef("description").GetString(root));
    }

    [Fact]
    public void GetBoolReadsARealBoolean()
    {
        var root = Json("""{ "private": true, "public": false }""");

        Assert.True(new FieldRef("private").GetBool(root));
        Assert.False(new FieldRef("public").GetBool(root));
    }

    [Fact]
    public void GetBoolComparesWhenAMatchValueIsGiven()
    {
        // GitLab sends a word, not a boolean. This is why the object form exists.
        var root = Json("""{ "visibility": "private" }""");

        Assert.True(new FieldRef("visibility", "private").GetBool(root));
        Assert.False(new FieldRef("visibility", "public").GetBool(root));
    }

    [Fact]
    public void MatchValueComparisonIgnoresCase()
    {
        var root = Json("""{ "visibility": "PRIVATE" }""");

        Assert.True(new FieldRef("visibility", "private").GetBool(root));
    }

    [Fact]
    public void GetBoolOnAMissingFieldIsFalse()
    {
        Assert.False(new FieldRef("absent").GetBool(Json("{}")));
    }

    [Fact]
    public void GetDateParsesIso8601AndRejectsRubbish()
    {
        var root = Json("""{ "updated_at": "2026-08-13T18:12:43Z", "bad": "whenever" }""");

        Assert.Equal(
            new DateTimeOffset(2026, 8, 13, 18, 12, 43, TimeSpan.Zero),
            new FieldRef("updated_at").GetDate(root)!.Value.ToUniversalTime());

        Assert.Null(new FieldRef("bad").GetDate(root));
    }

    // ---- The converter, which is what lets both spellings exist ----------------

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void ReadsTheShorthandStringForm()
    {
        var field = JsonSerializer.Deserialize<FieldRef>("\"owner.login\"", Options)!;

        Assert.Equal("owner.login", field.Path);
        Assert.Null(field.MatchValue);
    }

    [Fact]
    public void ReadsTheObjectForm()
    {
        var field = JsonSerializer.Deserialize<FieldRef>(
            """{ "path": "visibility", "equals": "private" }""", Options)!;

        Assert.Equal("visibility", field.Path);
        Assert.Equal("private", field.MatchValue);
    }

    [Fact]
    public void WritesTheShorthandWhenThereIsNothingToCompare()
    {
        Assert.Equal("\"clone_url\"", JsonSerializer.Serialize(new FieldRef("clone_url"), Options));
    }

    [Fact]
    public void RoundTripsBothForms()
    {
        foreach (var original in new[] { new FieldRef("clone_url"), new FieldRef("visibility", "private") })
        {
            var back = JsonSerializer.Deserialize<FieldRef>(
                JsonSerializer.Serialize(original, Options), Options)!;

            Assert.Equal(original.Path, back.Path);
            Assert.Equal(original.MatchValue, back.MatchValue);
        }
    }

    [Fact]
    public void ANumberIsNotAValidFieldMapping()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FieldRef>("42", Options));
    }
}
