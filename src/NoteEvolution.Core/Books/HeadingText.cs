using System.Text.RegularExpressions;

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
    /// marks (no <c>"## # Titel"</c>); the further text is the rest, trimmed, or <c>null</c> if blank.
    /// </summary>
    public static (string Title, string? Further) Split(string text)
    {
        var lines = text.Split('\n', 2);
        var title = HeadingMarks().Replace(lines[0].Trim(), "").Trim();
        return (title, lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]) ? lines[1].Trim() : null);
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
