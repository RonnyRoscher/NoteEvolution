using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NoteEvolution.Core.Books;

namespace NoteEvolution.Pdf;

/// <summary>Turns Logseq block text into reading text: links unwrapped, tags and block references dropped.</summary>
public static partial class ExportText
{
    /// <summary>Around the number of a citation taken out of the text while it is cleaned (private use, never in a text).</summary>
    private const char PlaceholderStart = '\uF8F0';
    private const char PlaceholderEnd = '\uF8F1';

    [GeneratedRegex("\uF8F0([0-9]+)\uF8F1")]
    private static partial Regex PlaceholderRegex();

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
    /// shrink to one and every line is trimmed. Line breaks stay. Well-formed block references whose id
    /// <paramref name="keepBlockRef"/> accepts stay (the PDF renders them as cross references); other text in double
    /// parentheses still goes. A source citation <c>{{quelle ((id)) Fundstelle}}</c> stays whole and untouched, with its
    /// reference and its locator as written (a <c>#3</c> or <c>[[x]]</c> in it stays).
    /// </summary>
    public static string Clean(string content, Func<Guid, bool>? keepBlockRef = null)
    {
        // The citations are taken out first and put back at the end, so none of the rules below reaches into them.
        var citations = SourceCitations.Find(content);
        var withPlaceholders = new StringBuilder();
        var position = 0;
        for (var i = 0; i < citations.Count; i++)
        {
            withPlaceholders.Append(content, position, citations[i].Start - position)
                .Append(PlaceholderStart).Append(i.ToString(CultureInfo.InvariantCulture)).Append(PlaceholderEnd);
            position = citations[i].Start + citations[i].Length;
        }

        withPlaceholders.Append(content, position, content.Length - position);
        var text = BracketTagRegex().Replace(withPlaceholders.ToString(), "");
        text = BlockRefRegex().Replace(
            text,
            m => keepBlockRef is not null && BlockRefs.LengthAt(m.Value, 0) == m.Length
                 && keepBlockRef(BlockRefs.Find(m.Value)[0].Id) ? m.Value : "");
        text = PageLinkRegex().Replace(text, "$1");
        text = TagRegex().Replace(text, "");
        text = RepeatedSpacesRegex().Replace(text, " ");
        text = string.Join("\n", text.Split('\n').Select(line => line.Trim())).Trim();
        return PlaceholderRegex().Replace(text, m =>
        {
            var citation = citations[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)];
            return content.Substring(citation.Start, citation.Length);
        });
    }
}
