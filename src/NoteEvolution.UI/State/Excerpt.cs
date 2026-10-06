namespace NoteEvolution.UI.State;

/// <summary>Short one-line previews of block content for lists in dialogs.</summary>
internal static class Excerpt
{
    private const int MaxLength = 120;

    /// <summary>The first line of <paramref name="content"/>, cut to a readable length.</summary>
    public static string FirstLine(string content)
    {
        var end = content.IndexOf('\n');
        var line = (end < 0 ? content : content[..end]).TrimEnd('\r').Trim();
        return line.Length <= MaxLength ? line : line[..(MaxLength - 1)] + "…";
    }
}
