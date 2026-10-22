using System.Text;
using System.Text.RegularExpressions;

namespace NoteEvolution.Core.Books;

/// <summary>A reference found in a text: the target id and the span of the whole <c>((…))</c>.</summary>
public readonly record struct BlockRef(Guid Id, int Start, int Length);

/// <summary>Block references <c>((uuid))</c> inside text: finding, formatting, retargeting and removing them.</summary>
public static partial class BlockRefs
{
    [GeneratedRegex(@"\(\(([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\)\)")]
    private static partial Regex RefRegex();

    /// <summary>All well-formed references in text order; anything else in double parentheses is ignored.</summary>
    public static IReadOnlyList<BlockRef> Find(string text)
    {
        var refs = new List<BlockRef>();
        foreach (Match m in RefRegex().Matches(text))
        {
            refs.Add(new BlockRef(Guid.ParseExact(m.Groups[1].Value, "D"), m.Index, m.Length));
        }

        return refs;
    }

    public static string Format(Guid id) => "((" + id.ToString("D") + "))";

    /// <summary>Replaces references whose id is a key of <paramref name="map"/> with a reference to the mapped id.</summary>
    public static string Retarget(string text, IReadOnlyDictionary<Guid, Guid> map) =>
        RefRegex().Replace(text, m =>
            map.TryGetValue(Guid.ParseExact(m.Groups[1].Value, "D"), out var to) ? Format(to) : m.Value);

    /// <summary>Removes the references to <paramref name="ids"/> plus one adjacent space, so no double space remains.</summary>
    public static string Remove(string text, IReadOnlySet<Guid> ids)
    {
        var sb = new StringBuilder(text);
        // Back to front so earlier offsets stay valid.
        foreach (var r in Find(text).Reverse().Where(r => ids.Contains(r.Id)))
        {
            var start = r.Start;
            var length = r.Length;
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
