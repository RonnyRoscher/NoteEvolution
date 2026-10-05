using System.Text;

namespace NoteEvolution.Core.Text;

/// <summary>A stretch of text with uniform emphasis.</summary>
public sealed record InlineRun(string Text, bool Bold, bool Italic);

/// <summary>
/// Reads and writes the emphasis subset of inline Markdown: <c>**bold**</c>, <c>*italic*</c> and <c>_italic_</c>.
/// Everything else (links, block refs, tags, escapes, code spans) is plain text and passes through unchanged.
/// A marker without a valid closing marker stays text.
/// </summary>
public static class InlineMarkdown
{
    public static IReadOnlyList<InlineRun> Parse(string text)
    {
        var runs = new List<InlineRun>();
        ParseInto(text, bold: false, italic: false, runs);
        return runs;
    }

    /// <summary>
    /// Writes <c>**</c> for bold and <c>*</c> for italic. A marker that stays active across consecutive runs
    /// is kept open, so nested emphasis (<c>*a **b** c*</c>) comes out as it would be typed.
    /// </summary>
    public static string Format(IEnumerable<InlineRun> runs)
    {
        const string BoldMarker = "**";
        const string ItalicMarker = "*";
        var sb = new StringBuilder();
        var open = new List<string>();
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;

            // Close everything from the lowest marker that is no longer wanted; wanted ones above it are reopened below.
            var unwanted = open.FindIndex(m => m == BoldMarker ? !run.Bold : !run.Italic);
            if (unwanted >= 0)
                CloseFrom(unwanted, open, sb);
            if (run.Bold) OpenIfClosed(BoldMarker, open, sb);
            if (run.Italic) OpenIfClosed(ItalicMarker, open, sb);
            sb.Append(run.Text);
        }
        CloseFrom(0, open, sb);
        return sb.ToString();
    }

    private static void OpenIfClosed(string marker, List<string> open, StringBuilder sb)
    {
        if (open.Contains(marker)) return;
        open.Add(marker);
        sb.Append(marker);
    }

    private static void CloseFrom(int index, List<string> open, StringBuilder sb)
    {
        for (var k = open.Count - 1; k >= index; k--)
            sb.Append(open[k]);
        open.RemoveRange(index, open.Count - index);
    }

    private static void ParseInto(string text, bool bold, bool italic, List<InlineRun> runs)
    {
        var plain = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var skip = PassThroughLength(text, i);
            if (skip > 0)
            {
                plain.Append(text, i, skip);
                i += skip;
                continue;
            }

            if (TryReadEmphasis(text, i, out var marker, out var content, out var end))
            {
                Flush(plain, bold, italic, runs);
                ParseInto(content, bold || marker.Length > 1, italic || marker is "*" or "_" or "***", runs);
                i = end;
                continue;
            }

            plain.Append(text[i]);
            i++;
        }
        Flush(plain, bold, italic, runs);
    }

    private static void Flush(StringBuilder plain, bool bold, bool italic, List<InlineRun> runs)
    {
        if (plain.Length == 0) return;
        var text = plain.ToString();
        plain.Clear();
        if (runs.Count > 0 && runs[^1].Bold == bold && runs[^1].Italic == italic)
            runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
        else
            runs.Add(new InlineRun(text, bold, italic));
    }

    /// <summary>Length of a backslash escape, link, block ref or code span starting at <paramref name="i"/>, else 0.</summary>
    private static int PassThroughLength(string text, int i)
    {
        if (text[i] == '\\')
            return i + 1 < text.Length ? 2 : 1;
        var closer = text[i] switch
        {
            '`' => "`",
            '[' when Has(text, i, "[[") => "]]",
            '(' when Has(text, i, "((") => "))",
            _ => null,
        };
        if (closer is null) return 0;
        var open = text[i] == '`' ? 1 : 2;
        var close = text.IndexOf(closer, i + open, StringComparison.Ordinal);
        return close < 0 ? 0 : close + closer.Length - i;
    }

    private static bool TryReadEmphasis(string text, int i, out string marker, out string content, out int end)
    {
        marker = "";
        content = "";
        end = i;
        var c = text[i];
        if (c != '*' && c != '_') return false;

        marker = Has(text, i, "***") ? "***" : Has(text, i, "**") ? "**" : c.ToString();
        var start = i + marker.Length;
        if (start >= text.Length || char.IsWhiteSpace(text[start])) return false;
        if (marker.Length == 1 && start < text.Length && text[start] == c) return false;
        if (c == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1])) return false;

        for (var j = start; j < text.Length;)
        {
            var skip = PassThroughLength(text, j);
            if (skip > 0)
            {
                j += skip;
                continue;
            }
            if (IsClosing(text, j, marker, start))
            {
                content = text[start..j];
                end = j + marker.Length;
                return true;
            }
            j++;
        }
        return false;
    }

    private static bool IsClosing(string text, int j, string marker, int contentStart)
    {
        if (j == contentStart || !Has(text, j, marker) || char.IsWhiteSpace(text[j - 1])) return false;
        var after = j + marker.Length;
        if (marker.Length > 1) return true;
        var c = marker[0];
        if (text[j - 1] == c || after < text.Length && text[after] == c) return false;
        return c != '_' || after >= text.Length || !char.IsLetterOrDigit(text[after]);
    }

    private static bool Has(string text, int i, string value) =>
        string.CompareOrdinal(text, i, value, 0, value.Length) == 0;
}
