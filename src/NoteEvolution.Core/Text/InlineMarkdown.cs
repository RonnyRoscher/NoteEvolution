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
    /// Writes <c>**</c> for bold and <c>*</c> for italic, such that <c>Parse(Format(runs))</c> equals
    /// <c>Normalize(runs)</c>. A marker that stays active across consecutive runs is kept open, so nested emphasis
    /// (<c>*a **b** c*</c>) comes out as it would be typed; where star runs at style boundaries would be read
    /// differently the writer falls back to underscores, and finally to writing every run on its own.
    /// </summary>
    public static string Format(IEnumerable<InlineRun> runs)
    {
        return Resolve(runs).Text;
    }

    /// <summary>
    /// Nested form with stars (that is how emphasis is usually typed), else with underscores for italic where a
    /// star cluster at a style boundary would be misread; null if neither reads back as <paramref name="normalized"/>.
    /// </summary>
    private static string? TryFormatNested(List<InlineRun> normalized)
    {
        var pieces = Layout(normalized);
        foreach (var underscores in new[] { false, true })
        {
            var candidate = Render(pieces, underscores);
            if (Parse(candidate).SequenceEqual(normalized))
                return candidate;
        }
        return null;
    }

    /// <summary>A piece of output: text, a bold marker, or one end of an italic pair (<see cref="Partner"/> = the other end).</summary>
    private sealed record Piece(string Text, bool IsText, bool IsItalicMarker, int Partner);

    private static string Render(List<Piece> pieces, bool underscores)
    {
        var sb = new StringBuilder();
        for (var p = 0; p < pieces.Count; p++)
        {
            var piece = pieces[p];
            if (piece.IsItalicMarker)
                sb.Append(underscores && UnderscoreIsLegal(pieces, Math.Min(p, piece.Partner), Math.Max(p, piece.Partner)) ? '_' : '*');
            else
                sb.Append(piece.Text);
        }
        return sb.ToString();
    }

    /// <summary>An underscore may neither open right after nor close right before a letter or digit.</summary>
    private static bool UnderscoreIsLegal(List<Piece> pieces, int open, int close) =>
        !(open > 0 && pieces[open - 1].IsText && char.IsLetterOrDigit(pieces[open - 1].Text[^1]))
        && !(close + 1 < pieces.Count && pieces[close + 1].IsText && char.IsLetterOrDigit(pieces[close + 1].Text[0]));

    /// <summary>Lays out the runs as text and markers, keeping a marker open across consecutive runs that share it.</summary>
    private static List<Piece> Layout(List<InlineRun> normalized)
    {
        var pieces = new List<Piece>();
        var open = new List<(bool Bold, int Index)>(); // innermost last
        void Close(int from)
        {
            for (var m = open.Count - 1; m >= from; m--)
            {
                var (bold, index) = open[m];
                if (bold)
                {
                    pieces.Add(new Piece("**", false, false, -1));
                }
                else
                {
                    pieces[index] = pieces[index] with { Partner = pieces.Count };
                    pieces.Add(new Piece("*", false, true, index));
                }
            }
            open.RemoveRange(from, open.Count - from);
        }

        for (var k = 0; k < normalized.Count; k++)
        {
            var run = normalized[k];

            // Close everything from the lowest marker that is no longer wanted; wanted ones above it are reopened below.
            var unwanted = open.FindIndex(m => m.Bold ? !run.Bold : !run.Italic);
            if (unwanted >= 0)
                Close(unwanted);

            var toOpen = new List<bool>();
            if (run.Bold && !open.Any(m => m.Bold)) toOpen.Add(true);
            if (run.Italic && !open.Any(m => !m.Bold)) toOpen.Add(false);
            // The marker that stays active longer goes outside, so it need not be closed and reopened later.
            if (toOpen.Count == 2 && Persistence(normalized, k, bold: false) > Persistence(normalized, k, bold: true))
                toOpen.Reverse();
            foreach (var bold in toOpen)
            {
                open.Add((bold, pieces.Count));
                pieces.Add(bold ? new Piece("**", false, false, -1) : new Piece("*", false, true, -1));
            }

            pieces.Add(new Piece(run.Text, true, false, -1));
        }
        Close(0);
        return pieces;
    }

    /// <summary>Every run closed before the next one starts; bold italic is <c>**_x_**</c>.</summary>
    private static string FormatFlat(List<InlineRun> normalized) =>
        string.Concat(normalized.Select(run => (run.Bold, run.Italic) switch
        {
            (true, true) => $"**_{run.Text}_**",
            (true, false) => $"**{run.Text}**",
            (false, true) => $"*{run.Text}*",
            _ => run.Text,
        }));

    /// <summary>
    /// The canonical form of a run list: empty runs dropped, adjacent runs with equal flags merged, and emphasis
    /// pulled off whitespace where it starts or ends (a marker cannot touch whitespace on its inner side). Whitespace
    /// inside an emphasis that continues into the neighbouring run stays (<c>*a **b** c*</c> is unchanged);
    /// where emphasis spans cross so that this cannot be written, all edge whitespace becomes plain.
    /// </summary>
    internal static List<InlineRun> Normalize(IEnumerable<InlineRun> runs) => Resolve(runs).Normalized;

    private static (List<InlineRun> Normalized, string Text) Resolve(IEnumerable<InlineRun> runs)
    {
        var loose = NormalizeCore(runs, keepContinuingEmphasis: true);
        if (TryFormatNested(loose) is { } text)
            return (loose, text);
        var strict = NormalizeCore(runs, keepContinuingEmphasis: false);
        return (strict, TryFormatNested(strict) ?? FormatFlat(strict));
    }

    private static List<InlineRun> NormalizeCore(IEnumerable<InlineRun> runs, bool keepContinuingEmphasis)
    {
        var merged = new List<InlineRun>();
        foreach (var run in runs)
            AddMerged(merged, run.Text, run.Bold, run.Italic);
        var keep = keepContinuingEmphasis;

        var result = new List<InlineRun>();
        for (var k = 0; k < merged.Count; k++)
        {
            var run = merged[k];
            var previous = k > 0 ? merged[k - 1] : new InlineRun("", false, false);
            var next = k + 1 < merged.Count ? merged[k + 1] : new InlineRun("", false, false);
            var core = run.Text.Trim();
            if (core.Length == 0)
            {
                AddMerged(result, run.Text, keep && run.Bold && previous.Bold && next.Bold, keep && run.Italic && previous.Italic && next.Italic);
                continue;
            }
            var start = run.Text.IndexOf(core, StringComparison.Ordinal);
            AddMerged(result, run.Text[..start], keep && run.Bold && previous.Bold, keep && run.Italic && previous.Italic);
            AddMerged(result, core, run.Bold, run.Italic);
            AddMerged(result, run.Text[(start + core.Length)..], keep && run.Bold && next.Bold, keep && run.Italic && next.Italic);
        }
        return result;
    }

    private static void AddMerged(List<InlineRun> runs, string text, bool bold, bool italic)
    {
        if (text.Length == 0) return;
        if (runs.Count > 0 && runs[^1].Bold == bold && runs[^1].Italic == italic)
            runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
        else
            runs.Add(new InlineRun(text, bold, italic));
    }

    private static int Persistence(List<InlineRun> runs, int from, bool bold)
    {
        var count = 0;
        while (from + count < runs.Count && (bold ? runs[from + count].Bold : runs[from + count].Italic))
            count++;
        return count;
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
        AddMerged(runs, text, bold, italic);
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
        if (c == '_')
        {
            // A lone underscore only: not part of a "__" run and not inside a word.
            if (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')) return false;
            if (i + 1 < text.Length && text[i + 1] == '_') return false;
            return TryReadAs("_", text, i, out marker, out content, out end);
        }
        if (c != '*') return false;

        // A run of three or more stars may open bold italic, or italic around bold, or bold around italic.
        string[] candidates = StarRun(text, i) >= 3 ? ["***", "*", "**"] : Has(text, i, "**") ? ["**"] : ["*"];
        foreach (var candidate in candidates)
            if (TryReadAs(candidate, text, i, out marker, out content, out end))
                return true;
        return false;
    }

    private static bool TryReadAs(string marker, string text, int i, out string usedMarker, out string content, out int end)
    {
        usedMarker = marker;
        content = "";
        end = i;
        var start = i + marker.Length;
        if (start >= text.Length || char.IsWhiteSpace(text[start])) return false;
        return marker[0] == '_'
            ? FindUnderscoreClose(text, start, out content, out end)
            : FindStarClose(text, start, marker, out content, out end);
    }

    private static bool FindUnderscoreClose(string text, int start, out string content, out int end)
    {
        content = "";
        end = start;
        for (var j = start; j < text.Length;)
        {
            var skip = PassThroughLength(text, j);
            if (skip > 0)
            {
                j += skip;
                continue;
            }
            var after = j + 1;
            if (text[j] == '_' && j > start && !char.IsWhiteSpace(text[j - 1]) && text[j - 1] != '_'
                && (after >= text.Length || !char.IsLetterOrDigit(text[after]) && text[after] != '_'))
            {
                content = text[start..j];
                end = after;
                return true;
            }
            j++;
        }
        return false;
    }

    /// <summary>
    /// Scans for the closing star marker. Star runs are split the way CommonMark does it: while looking for the end of
    /// bold, a lone <c>*</c> may open/close an inner italic; while looking for the end of italic, a <c>**</c> may
    /// open/close an inner bold. A run of three stars then closes both (<c>*a **b***</c>, <c>**a *b***</c>).
    /// </summary>
    private static bool FindStarClose(string text, int start, string marker, out string content, out int end)
    {
        content = "";
        end = start;
        var inner = false; // the other emphasis kind is currently open inside the content
        for (var j = start; j < text.Length;)
        {
            var skip = PassThroughLength(text, j);
            if (skip > 0)
            {
                j += skip;
                continue;
            }
            if (text[j] != '*')
            {
                j++;
                continue;
            }

            var run = StarRun(text, j);
            var canClose = j > start && !char.IsWhiteSpace(text[j - 1]);
            var canOpen = j + run < text.Length && !char.IsWhiteSpace(text[j + run]);
            // Where the closing marker starts, and how many stars of the run it takes.
            var closeAt = -1;
            switch (marker)
            {
                case "***":
                    if (run >= 3 && canClose) closeAt = j;
                    break;
                case "**":
                    if (run == 1) inner = ToggleInner(inner, canOpen, canClose);
                    else if (run == 2 || run >= 4 && !inner) closeAt = canClose ? j : -1;
                    else if (run == 3 && canClose) closeAt = inner ? j + 1 : j;
                    else if (run >= 4 && inner && canClose) closeAt = j + 1;
                    break;
                default: // "*"
                    if (run == 1) closeAt = !inner && canClose ? j : -1;
                    else if (run == 2) inner = ToggleInner(inner, canOpen, canClose);
                    else if (run == 3 && canClose) closeAt = inner ? j + 2 : j;
                    break;
            }
            if (closeAt >= 0)
            {
                content = text[start..closeAt];
                end = closeAt + marker.Length;
                return true;
            }
            j += run;
        }
        return false;
    }

    private static bool ToggleInner(bool inner, bool canOpen, bool canClose) =>
        inner ? !canClose : canOpen;

    private static int StarRun(string text, int i)
    {
        var n = 0;
        while (i + n < text.Length && text[i + n] == '*') n++;
        return n;
    }

    private static bool Has(string text, int i, string value) =>
        string.CompareOrdinal(text, i, value, 0, value.Length) == 0;
}
