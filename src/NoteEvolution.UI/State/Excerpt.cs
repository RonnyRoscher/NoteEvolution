namespace NoteEvolution.UI.State;

/// <summary>Short one-line previews of block content for lists in dialogs.</summary>
internal static class Excerpt
{
    private const int MaxLength = 120;

    /// <summary>The first line of <paramref name="content"/>, cut to <paramref name="maxLength"/> characters at most.</summary>
    public static string FirstLine(string content, int maxLength = MaxLength)
    {
        var end = content.IndexOf('\n');
        var line = (end < 0 ? content : content[..end]).TrimEnd('\r').Trim();
        return line.Length <= maxLength ? line : line[..(maxLength - 1)] + "…";
    }
}
