using System.Text.RegularExpressions;

namespace NoteEvolution.Core.Text;

/// <summary>
/// Masks editor text lines that Logseq would read as structure (ruling R14): a line that, after optional
/// spaces or tabs, is a bullet (<c>-</c> or <c>*</c> alone or followed by a space) or a code fence
/// (<c>```</c>), or that starts with a heading marker (<c>#</c> to <c>######</c> and a space), gets a leading
/// backslash. Lines that already start with backslashes before such syntax get one more, so that
/// <c>Unescape(Escape(text)) == text</c> for every text.
/// </summary>
public static partial class BlockTextEscape
{
    private const string Syntax = @"([ \t]*([-*]( |$)|```)|#{1,6} )";

    [GeneratedRegex(@"^\\*" + Syntax)]
    private static partial Regex EscapeRegex();

    [GeneratedRegex(@"^\\+" + Syntax)]
    private static partial Regex UnescapeRegex();

    /// <summary>Editor text → block content: every line that needs it gets a leading backslash.</summary>
    public static string Escape(string text) =>
        MapLines(text, line => EscapeRegex().IsMatch(line) ? "\\" + line : line);

    /// <summary>Block content → editor text: removes exactly one leading backslash where <see cref="Escape"/> added one.</summary>
    public static string Unescape(string text) =>
        MapLines(text, line => UnescapeRegex().IsMatch(line) ? line[1..] : line);

    private static string MapLines(string text, Func<string, string> map) =>
        string.Join("\n", text.Split('\n').Select(map));
}
