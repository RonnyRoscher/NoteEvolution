namespace NoteEvolution.Core.Books;

/// <summary>Builds the content of a heading block (<c>"#" * level + " " + title</c>) from an existing one.</summary>
internal static class HeadingText
{
    /// <summary>
    /// The title as it stands in the first line of a heading with <paramref name="level"/> hashes, spaces included
    /// (<see cref="OutlineNode.Title"/> is trimmed); <see cref="WithTitle"/> writes it back unchanged.
    /// </summary>
    public static string TitleOf(string content, int level) => content.Split('\n', 2)[0][(level + 1)..];

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
