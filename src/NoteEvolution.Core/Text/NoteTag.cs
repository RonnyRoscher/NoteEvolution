using System.Text.RegularExpressions;

namespace NoteEvolution.Core.Text;

/// <summary>The <c>#notiz</c> marker that flags a block as a note.</summary>
public static partial class NoteTag
{
    public const string Tag = "#notiz";

    [GeneratedRegex(@"(^|\s)#notiz(\s|$)")]
    private static partial Regex HasRegex();

    // Whitespace on the tag's own line is swallowed; a line break next to the tag is left to the caller's text.
    [GeneratedRegex(@"[ \t]*(?<!\S)#notiz(?!\S)[ \t]*")]
    private static partial Regex TokenRegex();

    public static bool Has(string content) => HasRegex().IsMatch(content);

    /// <summary>Removes every <c>#notiz</c> token and the spaces around it (one space remains between neighbouring words), and trims the ends.</summary>
    public static string Strip(string content) =>
        TokenRegex().Replace(content, m =>
        {
            var before = m.Index > 0 ? content[m.Index - 1] : '\n';
            var after = m.Index + m.Length < content.Length ? content[m.Index + m.Length] : '\n';
            return before is '\n' or '\r' || after is '\n' or '\r' ? "" : " ";
        }).Trim();

    public static string Add(string text) => text + " " + Tag;
}
