using System.Text.RegularExpressions;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Format;

/// <summary>Line-level Logseq Markdown rules shared by the parser and the model.</summary>
internal static partial class LogseqSyntax
{
    /// <summary>Indentation width of a tab; a space counts 1.</summary>
    public const int TabWidth = 4;

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<bullet>[-*])( (?<text>.*))?$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^\s*(?<key>[^\s:]+)::( (?<value>.*))?$")]
    private static partial Regex PropertyRegex();

    public static bool TryParseBullet(string line, out string indent, out char bullet, out string text)
    {
        var match = BulletRegex().Match(line);
        if (!match.Success)
        {
            indent = "";
            bullet = '\0';
            text = "";
            return false;
        }

        indent = match.Groups["indent"].Value;
        bullet = match.Groups["bullet"].Value[0];
        text = match.Groups["text"].Value;
        return true;
    }

    public static bool IsBullet(string line) => BulletRegex().IsMatch(line);

    public static BlockProperty? TryParseProperty(string line)
    {
        var match = PropertyRegex().Match(line);
        return match.Success
            ? new BlockProperty(match.Groups["key"].Value, match.Groups["value"].Value.Trim())
            : null;
    }

    /// <summary>
    /// Locates the property section of the block whose bullet line is <c>lines[bulletIndex]</c>:
    /// the run of property lines directly after the bullet line or, if the bullet line opens a
    /// code fence, directly after the line that closes that fence. Indices are relative to the bullet line.
    /// </summary>
    public static PropertySection FindPropertySection(IReadOnlyList<RawLine> lines, int bulletIndex)
    {
        var bulletText = TryParseBullet(lines[bulletIndex].Text, out _, out _, out var text) ? text : lines[bulletIndex].Text;
        var start = bulletIndex + 1;
        var run = FenceOpening(bulletText);
        if (run > 0)
        {
            while (start < lines.Count && !IsFenceClosing(lines[start].Text, run))
            {
                start++;
            }

            if (start == lines.Count)
            {
                return new PropertySection(start - bulletIndex, 0, FenceUnclosed: true);
            }

            start++;
        }

        var end = start;
        while (end < lines.Count && TryParseProperty(lines[end].Text) is not null)
        {
            end++;
        }

        return new PropertySection(start - bulletIndex, end - start, FenceUnclosed: false);
    }

    public static int IndentWidth(string indent)
    {
        var width = 0;
        foreach (var c in indent)
        {
            width += c == '\t' ? TabWidth : 1;
        }

        return width;
    }

    /// <summary>
    /// If <paramref name="text"/> (after its indentation) opens a code fence, returns the length of
    /// its backtick run (at least 3); otherwise 0. Like CommonMark, an info string with a backtick
    /// (e.g. <c>```inline```</c>) does not open a fence.
    /// </summary>
    public static int FenceOpening(string text)
    {
        var rest = text.TrimStart(' ', '\t');
        var run = BacktickRun(rest);
        return run >= 3 && !rest.AsSpan(run).Contains('`') ? run : 0;
    }

    /// <summary>
    /// Whether <paramref name="text"/> closes a fence opened with <paramref name="openingRun"/> backticks:
    /// at least as many backticks, followed only by whitespace.
    /// </summary>
    public static bool IsFenceClosing(string text, int openingRun)
    {
        var rest = text.TrimStart(' ', '\t');
        var run = BacktickRun(rest);
        return run >= openingRun && string.IsNullOrWhiteSpace(rest[run..]);
    }

    private static int BacktickRun(string text)
    {
        var run = 0;
        while (run < text.Length && text[run] == '`')
        {
            run++;
        }

        return run;
    }
}
