using System;
using System.Collections.Generic;
using GitGui.Models;

namespace GitGui.Services;

/// <summary>
/// Turns the unified-diff text libgit2 produces into the <see cref="DiffLine"/>
/// rows the diff view renders.
/// </summary>
/// <remarks>
/// libgit2 hands back a patch as plain text. We parse it rather than render it raw
/// so the view can style each line and show both old and new line numbers, which a
/// plain text block can't do.
/// </remarks>
public static class UnifiedDiffParser
{
    /// <summary>Caps how much of a very large patch we materialise into the UI.</summary>
    public const int MaxLines = 4000;

    public static IReadOnlyList<DiffLine> Parse(string? patchText)
    {
        var lines = new List<DiffLine>();

        if (string.IsNullOrEmpty(patchText))
            return lines;

        var oldNo = 0;
        var newNo = 0;

        foreach (var raw in patchText.Split('\n'))
        {
            if (lines.Count >= MaxLines)
            {
                lines.Add(new DiffLine
                {
                    Kind = DiffLineKind.HunkHeader,
                    Text = $"… diff truncated at {MaxLines} lines",
                });
                break;
            }

            // Strip a trailing \r so CRLF repositories don't render stray glyphs.
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;

            // File headers repeat information already shown in the view's own
            // header bar, so they are dropped.
            if (line.StartsWith("diff --git ", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("--- ", StringComparison.Ordinal)
                || line.StartsWith("+++ ", StringComparison.Ordinal)
                || line.StartsWith("new file mode", StringComparison.Ordinal)
                || line.StartsWith("deleted file mode", StringComparison.Ordinal)
                || line.StartsWith("similarity index", StringComparison.Ordinal)
                || line.StartsWith("rename from", StringComparison.Ordinal)
                || line.StartsWith("rename to", StringComparison.Ordinal)
                || line.StartsWith("old mode", StringComparison.Ordinal)
                || line.StartsWith("new mode", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (TryParseHunkHeader(line, out var oldStart, out var newStart))
                {
                    oldNo = oldStart;
                    newNo = newStart;
                }

                lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = line });
                continue;
            }

            if (line.Length == 0)
                continue;

            switch (line[0])
            {
                case '+':
                    lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Added,
                        Text = line[1..],
                        NewNumber = newNo.ToString(),
                    });
                    newNo++;
                    break;

                case '-':
                    lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Removed,
                        Text = line[1..],
                        OldNumber = oldNo.ToString(),
                    });
                    oldNo++;
                    break;

                case ' ':
                    lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Context,
                        Text = line[1..],
                        OldNumber = oldNo.ToString(),
                        NewNumber = newNo.ToString(),
                    });
                    oldNo++;
                    newNo++;
                    break;

                case '\\':
                    // "\ No newline at end of file"
                    lines.Add(new DiffLine { Kind = DiffLineKind.Context, Text = line });
                    break;
            }
        }

        return lines;
    }

    /// <summary>
    /// Reads the starting line numbers out of a hunk header such as
    /// <c>@@ -14,9 +14,18 @@ optional context</c>.
    /// </summary>
    private static bool TryParseHunkHeader(string header, out int oldStart, out int newStart)
    {
        oldStart = 0;
        newStart = 0;

        var minus = header.IndexOf('-');
        var plus = header.IndexOf('+');
        if (minus < 0 || plus < 0)
            return false;

        oldStart = ReadNumber(header, minus + 1);
        newStart = ReadNumber(header, plus + 1);
        return true;
    }

    private static int ReadNumber(string text, int start)
    {
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
            end++;

        return end > start && int.TryParse(text[start..end], out var value) ? value : 0;
    }
}
