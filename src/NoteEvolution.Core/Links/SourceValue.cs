using System.Text.RegularExpressions;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Links;

/// <summary>
/// The value of a <c>source::</c> property: block references <c>((id)), ((id))</c>.
/// Parsing is lenient (extra whitespace; fragments that are not a block reference in Guid "D" format are ignored);
/// formatting is canonical.
/// </summary>
public static partial class SourceValue
{
    [GeneratedRegex(@"\(\([ \t]*(?<id>[^()]*?)[ \t]*\)\)")]
    private static partial Regex RefRegex();

    public static IReadOnlyList<Guid> Parse(string value)
    {
        var ids = new List<Guid>();
        foreach (Match match in RefRegex().Matches(value))
            if (Guid.TryParseExact(match.Groups["id"].Value, "D", out var id))
                ids.Add(id);
        return ids;
    }

    public static string Format(IEnumerable<Guid> ids) =>
        string.Join(", ", ids.Select(id => $"(({id:D}))"));

    /// <summary>
    /// The blocks of <paramref name="root"/>'s subtree (root first, depth-first) that have an <c>id::</c> and at least
    /// one source, with their distinct sources.
    /// </summary>
    public static IEnumerable<(Guid Id, IReadOnlyList<Guid> Sources)> LinkedBlocksIn(Block root)
    {
        if (root.Id is { } id && Parse(root.GetProperty("source") ?? "") is { Count: > 0 } sources)
            yield return (id, [.. sources.Distinct()]);
        foreach (var child in root.Children)
            foreach (var linked in LinkedBlocksIn(child))
                yield return linked;
    }
}
