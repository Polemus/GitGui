using Omnigit.Models;
using Omnigit.Services;

namespace Omnigit.Tests;

/// <summary>
/// The parser turns libgit2's patch text into rows the view renders. It is a pure
/// function over a string, so everything here is real patch output pasted in.
/// </summary>
public class UnifiedDiffParserTests
{
    [Fact]
    public void EmptyInputProducesNoLines()
    {
        Assert.Empty(UnifiedDiffParser.Parse(null));
        Assert.Empty(UnifiedDiffParser.Parse(string.Empty));
    }

    [Fact]
    public void FileHeadersAreDropped()
    {
        const string patch = """
            diff --git a/README.md b/README.md
            index 1234567..89abcde 100644
            --- a/README.md
            +++ b/README.md
            @@ -1,2 +1,2 @@
             kept
            -old
            +new
            """;

        var lines = UnifiedDiffParser.Parse(patch);

        // Header, context, removed, added - and none of the four file-header lines.
        Assert.Equal(4, lines.Count);
        Assert.Equal(DiffLineKind.HunkHeader, lines[0].Kind);
        Assert.DoesNotContain(lines, l => l.Text.StartsWith("diff --git"));
        Assert.DoesNotContain(lines, l => l.Text.StartsWith("index "));
    }

    [Fact]
    public void LineNumbersRunSeparatelyForEachSide()
    {
        const string patch = """
            @@ -10,3 +20,3 @@
             context
            -removed
            +added
            """;

        var lines = UnifiedDiffParser.Parse(patch);

        var context = lines[1];
        Assert.Equal("10", context.OldNumber);
        Assert.Equal("20", context.NewNumber);

        // A removed line advances only the old side.
        var removed = lines[2];
        Assert.Equal(DiffLineKind.Removed, removed.Kind);
        Assert.Equal("11", removed.OldNumber);
        Assert.Equal(string.Empty, removed.NewNumber);

        // An added line advances only the new side.
        var added = lines[3];
        Assert.Equal(DiffLineKind.Added, added.Kind);
        Assert.Equal(string.Empty, added.OldNumber);
        Assert.Equal("21", added.NewNumber);
    }

    [Fact]
    public void MarkerCharacterIsStrippedFromTheText()
    {
        var lines = UnifiedDiffParser.Parse("@@ -1,1 +1,1 @@\n+    indented\n");

        var added = Assert.Single(lines, l => l.Kind == DiffLineKind.Added);

        // The leading + goes, but the code's own indentation must survive it.
        Assert.Equal("    indented", added.Text);
    }

    [Fact]
    public void CarriageReturnsAreStripped()
    {
        var lines = UnifiedDiffParser.Parse("@@ -1,1 +1,1 @@\r\n+text\r\n");

        var added = Assert.Single(lines, l => l.Kind == DiffLineKind.Added);
        Assert.Equal("text", added.Text);
    }

    [Fact]
    public void SecondHunkRestartsNumberingFromItsOwnHeader()
    {
        const string patch = """
            @@ -1,1 +1,1 @@
             first
            @@ -100,1 +200,1 @@
             second
            """;

        var lines = UnifiedDiffParser.Parse(patch);

        Assert.Equal("1", lines[1].OldNumber);
        Assert.Equal("100", lines[3].OldNumber);
        Assert.Equal("200", lines[3].NewNumber);
    }

    [Fact]
    public void AHunkHeaderWithoutCountsStillSetsTheStart()
    {
        // Git omits the count when a hunk is one line long.
        var lines = UnifiedDiffParser.Parse("@@ -5 +7 @@\n context\n");

        Assert.Equal("5", lines[1].OldNumber);
        Assert.Equal("7", lines[1].NewNumber);
    }

    [Fact]
    public void OversizedPatchesAreTruncatedRatherThanRendered()
    {
        var body = string.Join('\n', Enumerable.Repeat("+line", UnifiedDiffParser.MaxLines + 500));
        var lines = UnifiedDiffParser.Parse("@@ -1,1 +1,1 @@\n" + body);

        // The cap, plus the row explaining the cap.
        Assert.Equal(UnifiedDiffParser.MaxLines + 1, lines.Count);
        Assert.Contains("truncated", lines[^1].Text);
        Assert.Equal(DiffLineKind.HunkHeader, lines[^1].Kind);
    }

    [Fact]
    public void MarkerReflectsTheKind()
    {
        var lines = UnifiedDiffParser.Parse("@@ -1,3 +1,3 @@\n context\n-gone\n+here\n");

        Assert.Equal(" ", lines[1].Marker);
        Assert.Equal("-", lines[2].Marker);
        Assert.Equal("+", lines[3].Marker);
    }
}
