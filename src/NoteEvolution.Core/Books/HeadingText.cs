using System.Text.RegularExpressions;
using NoteEvolution.Core.Text;

namespace NoteEvolution.Core.Books;

/// <summary>
/// Builds the content of a heading block (<c>"#" * level + " " + title</c>) from an existing one, and splits a block's
/// text into the title and the further text of a heading made from it.
/// </summary>
internal static partial class HeadingText
{
    /// <summary>The <c>#</c> marks that make a line a heading (<c>"# "</c> to <c>"###### "</c>).</summary>
    [GeneratedRegex("^#{1,6} +")]
    private static partial Regex HeadingMarks();

    /// <summary>
    /// Splits the text of a block a heading is made from: the title is its first line, trimmed and without heading
    /// marks (no <c>"## # Titel"</c>); the further text is the rest without its leading and trailing blank lines (the
    /// indentation of its lines is kept), or <c>null</c> if blank.
    /// </summary>
    public static (string Title, string? Further) Split(string text)
    {
        var lines = text.Split('\n');
        var title = HeadingMarks().Replace(lines[0].Trim(), "").Trim();
        var further = lines.Skip(1).SkipWhile(string.IsNullOrWhiteSpace).Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse().ToList();
        return (title, further.Count > 0 ? string.Join("\n", further) : null);
    }

    /// <summary>
    /// <paramref name="content"/> for a block that becomes a text block, its first line escaped
    /// (<see cref="BlockTextEscape.Escape"/>) so that it does not read as a heading, bullet or property and the editor
    /// shows it as before. <paramref name="shownUnescaped"/>: the block was shown unescaped (a sub-point), so a line
    /// escaped already stays as it is; else it was shown as it stands (a heading's title).
    /// </summary>
    public static string AsTextBlockContent(string content, bool shownUnescaped)
    {
        var lines = content.Split('\n', 2);
        var first = BlockTextEscape.Escape(shownUnescaped ? BlockTextEscape.Unescape(lines[0]) : lines[0]);
        return lines.Length > 1 ? first + "\n" + lines[1] : first;
    }

    /// <summary>Whether <paramref name="text"/> starts with heading marks, so a block with it would be read as a heading.</summary>
    public static bool StartsAsHeading(string text) => HeadingMarks().IsMatch(text);

    /// <summary>
    /// The title as it stands in the first line of a heading with <paramref name="level"/> hashes, spaces included
    /// (<see cref="OutlineNode.Title"/> is trimmed); <see cref="WithTitle"/> writes it back unchanged.
    /// </summary>
    public static string TitleOf(string content, int level) => content.Split('\n', 2)[0][(level + 1)..];

    /// <summary>
    /// The heading's text without its <paramref name="level"/> hashes and the space after them (an empty heading may
    /// lack that space); further lines are kept.
    /// </summary>
    public static string WithoutMarks(string content, int level) =>
        content.Length > level && content[level] == ' ' ? content[(level + 1)..] : content[level..];

    /// <summary>
    /// <paramref name="content"/> with its first line replaced by <paramref name="level"/> hashes and
    /// <paramref name="title"/>; further lines are kept.
    /// </summary>
    public static string WithTitle(string content, int level, string title)
    {
        var lines = content.Split('\n');
        lines[0] = new string('#', level) + " " + title;
        return string.Join("\n", lines);
    }

    /// <summary>
    /// <paramref name="content"/> with the <paramref name="oldLevel"/> hashes of its first line replaced by
    /// <paramref name="newLevel"/> hashes; the title and further lines stay byte-identical.
    /// </summary>
    public static string WithLevel(string content, int oldLevel, int newLevel) =>
        new string('#', newLevel) + content[oldLevel..];
}
