using System.Globalization;
using NoteEvolution.Core.Books;

namespace NoteEvolution.AI.Understanding;

/// <summary>
/// A section's lasting name across sessions (block keys are new on every start): its titles from the top level down, each
/// with <c>#</c> and its index among the siblings of the same title, joined with <c>" › "</c>, e.g.
/// <c>"Schlaf#0 › Träume#0"</c>. The root (prologue) is the empty path. Pure.
/// </summary>
public static class SectionPath
{
    private const string Separator = " › ";

    /// <summary>The path of <paramref name="node"/>.</summary>
    public static string Of(OutlineNode node)
    {
        var parts = new List<string>();
        for (var n = node; n.Parent is { } parent; n = parent)
        {
            var index = parent.Children.TakeWhile(child => child != n).Count(child => child.Title == n.Title);
            parts.Insert(0, $"{n.Title}#{index}");
        }

        return string.Join(Separator, parts);
    }

    /// <summary>The section of <paramref name="book"/> with <paramref name="path"/>; null if there is none.</summary>
    public static OutlineNode? Find(Book book, string path)
    {
        var node = book.Root;
        if (path.Length == 0)
        {
            return node;
        }

        foreach (var part in path.Split(Separator))
        {
            var hash = part.LastIndexOf('#');
            if (hash < 0 || !int.TryParse(part[(hash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                return null;
            }

            var title = part[..hash];
            node = node.Children.Where(child => child.Title == title).ElementAtOrDefault(index);
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }
}
