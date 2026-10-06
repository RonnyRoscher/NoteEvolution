using System.Text.RegularExpressions;

namespace NoteEvolution.Pdf;

/// <summary>Turns Logseq block text into reading text: links unwrapped, tags and block references dropped.</summary>
public static partial class ExportText
{
    [GeneratedRegex(@"#\[\[[^\]]*\]\]")]
    private static partial Regex BracketTagRegex();

    [GeneratedRegex(@"\(\([0-9a-fA-F-]+\)\)")]
    private static partial Regex BlockRefRegex();

    [GeneratedRegex(@"\[\[([^\]]*)\]\]")]
    private static partial Regex PageLinkRegex();

    [GeneratedRegex(@"(?<!\S)#[\p{L}\p{N}_/-]+")]
    private static partial Regex TagRegex();

    [GeneratedRegex(" {2,}")]
    private static partial Regex RepeatedSpacesRegex();

    /// <summary>
    /// <c>[[x]]</c> becomes <c>x</c>; <c>#[[x]]</c>, <c>#tag</c> and <c>((uuid))</c> are removed; runs of spaces
    /// shrink to one and every line is trimmed. Line breaks stay.
    /// </summary>
    public static string Clean(string content)
    {
        var text = BracketTagRegex().Replace(content, "");
        text = BlockRefRegex().Replace(text, "");
        text = PageLinkRegex().Replace(text, "$1");
        text = TagRegex().Replace(text, "");
        text = RepeatedSpacesRegex().Replace(text, " ");
        return string.Join("\n", text.Split('\n').Select(line => line.Trim())).Trim();
    }
}
