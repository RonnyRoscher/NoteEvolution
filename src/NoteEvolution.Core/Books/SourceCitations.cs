using System.Text;
using System.Text.RegularExpressions;

namespace NoteEvolution.Core.Books;

/// <summary>A source citation found in a text: the source id, the locator (<c>null</c> if none) and the span of the whole macro.</summary>
public readonly record struct SourceCitation(Guid Id, string? Locator, int Start, int Length);

/// <summary>
/// The source citation macro <c>{{quelle ((id))}}</c> or <c>{{quelle ((id)) Fundstelle}}</c> inside text: finding,
/// formatting and removing it. The reference inside the macro is part of the macro, not a block reference
/// (see <see cref="BlockRefs.Find"/>).
/// </summary>
public static partial class SourceCitations
{
    private const string Guid36 = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    [GeneratedRegex(@"\{\{quelle \(\((" + Guid36 + @")\)\)(?: ([^}\r\n]*))?\}\}")]
    private static partial Regex MacroRegex();

    [GeneratedRegex(@"\G\{\{quelle \(\(" + Guid36 + @"\)\)(?: [^}\r\n]*)?\}\}")]
    private static partial Regex MacroAtRegex();

    /// <summary>All well-formed citations in text order; the locator is trimmed, an empty one is <c>null</c>.</summary>
    public static IReadOnlyList<SourceCitation> Find(string text)
    {
        var citations = new List<SourceCitation>();
        foreach (Match m in MacroRegex().Matches(text))
        {
            var locator = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
            citations.Add(new SourceCitation(
                Guid.ParseExact(m.Groups[1].Value, "D"), locator.Length > 0 ? locator : null, m.Index, m.Length));
        }

        return citations;
    }

    /// <summary>The length of the citation that starts at <paramref name="index"/> of <paramref name="text"/>; 0 if none starts there.</summary>
    public static int LengthAt(string text, int index) =>
        index + 1 < text.Length && text[index] == '{' && text[index + 1] == '{' && MacroAtRegex().Match(text, index) is { Success: true } m
            ? m.Length
            : 0;

    /// <summary>
    /// The macro for <paramref name="id"/>. The locator is trimmed and written on one line; a <c>}</c> in it would end
    /// the macro early and is dropped; an empty one is left out.
    /// </summary>
    public static string Format(Guid id, string? locator)
    {
        var clean = string.Join(' ', (locator ?? "").Split(['\r', '\n', '}'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return "{{quelle " + BlockRefs.Format(id) + (clean.Length > 0 ? " " + clean : "") + "}}";
    }

    /// <summary>Removes the citations of <paramref name="ids"/> plus one adjacent space, so no double space remains.</summary>
    public static string Remove(string text, IReadOnlySet<Guid> ids)
    {
        var sb = new StringBuilder(text);
        // Back to front so earlier offsets stay valid.
        foreach (var c in Find(text).Reverse().Where(c => ids.Contains(c.Id)))
        {
            var start = c.Start;
            var length = c.Length;
            if (start > 0 && sb[start - 1] == ' ')
            {
                start--;
                length++;
            }
            else if (start + length < sb.Length && sb[start + length] == ' ')
            {
                length++;
            }

            sb.Remove(start, length);
        }

        return sb.ToString();
    }
}
