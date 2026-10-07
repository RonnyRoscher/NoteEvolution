using System.Text.RegularExpressions;

namespace NoteEvolution.Core.Text;

/// <summary>
/// Masks editor text lines that Logseq would read as structure (rulings R14, R15, R16). Lines are separated by
/// <c>"\n"</c>.
/// <list type="bullet">
/// <item>A line that, after optional spaces or tabs, is a bullet (<c>-</c> or <c>*</c> alone or followed by a
/// space) or a code fence (<c>```</c>), or that starts with a heading marker (<c>#</c> to <c>######</c> and a
/// space), gets a leading backslash.</item>
/// <item>A property-shaped line (<c>key:: value</c>) gets a backslash between the colons (<c>key:\: value</c>).</item>
/// </list>
/// Lines that already carry backslashes at such a place get one more, so that
/// <c>Unescape(Escape(text)) == text</c> for every text.
/// </summary>
public static partial class BlockTextEscape
{
    private const string Syntax = @"([ \t]*([-*]( |$)|```)|#{1,6} )";

    [GeneratedRegex(@"^\\*" + Syntax)]
    private static partial Regex EscapeRegex();

    [GeneratedRegex(@"^\\+" + Syntax)]
    private static partial Regex UnescapeRegex();

    // The key cannot contain a colon, so the first colon of the line is the one before the backslashes.
    [GeneratedRegex(@"^\s*[^\s:]+:\\*:( |$)")]
    private static partial Regex PropertyEscapeRegex();

    [GeneratedRegex(@"^\s*[^\s:]+:\\+:( |$)")]
    private static partial Regex PropertyUnescapeRegex();

    /// <summary>Editor text → block content: every line that needs it gets its backslashes.</summary>
    public static string Escape(string text) => MapLines(text, line =>
    {
        if (PropertyEscapeRegex().IsMatch(line))
        {
            line = line.Insert(line.IndexOf(':') + 1, "\\");
        }

        return EscapeRegex().IsMatch(line) ? "\\" + line : line;
    });

    /// <summary>Block content → editor text: removes exactly the backslashes <see cref="Escape"/> added.</summary>
    public static string Unescape(string text) => MapLines(text, line =>
    {
        if (UnescapeRegex().IsMatch(line))
        {
            line = line[1..];
        }

        return PropertyUnescapeRegex().IsMatch(line) ? line.Remove(line.IndexOf(':') + 1, 1) : line;
    });

    private static string MapLines(string text, Func<string, string> map) =>
        string.Join("\n", text.Split('\n').Select(map));
}
