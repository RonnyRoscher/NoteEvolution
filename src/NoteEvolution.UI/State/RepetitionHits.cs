using NoteEvolution.Core.Books;
using NoteEvolution.Core.Repetitions;

namespace NoteEvolution.UI.State;

/// <summary>
/// The hits the repetitions tab steps through (repetitions tab spec 4): their order and filter, which hit the tab stays
/// at when the book or its analysis changes, and which side of a hit is the origin.
/// </summary>
public static class RepetitionHits
{
    /// <summary>
    /// The hits of <paramref name="set"/> the tab shows, literal ones first, then semantic ones, each by the position of
    /// the earlier side (the order of <see cref="RepetitionSet.All"/>): accepted ones only with
    /// <paramref name="showAccepted"/>, and with a <paramref name="chapter"/> only those with a side in it.
    /// </summary>
    public static IReadOnlyList<Repetition> Ordered(RepetitionSet set, Book book, bool showAccepted, OutlineNode? chapter)
    {
        var keys = chapter is null ? null : TextBlockKeys(chapter);
        return
        [
            .. set.All.Where(r => (showAccepted || !r.Accepted) && (keys is null || keys.Contains(r.A) || keys.Contains(r.B))),
        ];
    }

    /// <summary>
    /// The index in <paramref name="hits"/> to stay at after a change: that of the same pair (kind and both sides) as
    /// <paramref name="focus"/>; else the first hit in the order of the list from the place of the former origin on (a
    /// hit of the same kind whose earlier side is not before it, or any hit of a later kind); else the last one. Without
    /// the former origin its other side counts; without both (another book) the first hit. −1 if there are no hits.
    /// </summary>
    public static int IndexAfterChange(IReadOnlyList<Repetition> hits, RepetitionFocus focus, Book book)
    {
        if (hits.Count == 0)
        {
            return -1;
        }

        for (var i = 0; i < hits.Count; i++)
        {
            if (hits[i].Kind == focus.Kind && hits[i].A == focus.A && hits[i].B == focus.B)
            {
                return i;
            }
        }

        var positions = Positions(book);
        if (!positions.TryGetValue(focus.Origin, out var place) && !positions.TryGetValue(focus.Other, out place))
        {
            // Neither side is in the book (another book): its first hit.
            return 0;
        }

        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            if (hit.Kind > focus.Kind
                || (hit.Kind == focus.Kind && positions.GetValueOrDefault(hit.A, int.MaxValue) >= place))
            {
                return i;
            }
        }

        return hits.Count - 1;
    }

    /// <summary>The focus on <paramref name="hit"/> with <paramref name="origin"/> as its origin if that is its later side B; else A.</summary>
    public static RepetitionFocus FocusOn(Repetition hit, Guid? origin) =>
        new(hit.Kind, hit.A, hit.B, Swapped: origin == hit.B && hit.A != hit.B);

    /// <summary>The keys of the text blocks in <paramref name="section"/> and below it.</summary>
    public static HashSet<Guid> TextBlockKeys(OutlineNode section)
    {
        var keys = new HashSet<Guid>();
        foreach (var text in TextBlocksIn(section))
        {
            keys.Add(text.Key);
        }

        return keys;
    }

    /// <summary>The position of each text block of <paramref name="book"/> in book order.</summary>
    private static Dictionary<Guid, int> Positions(Book book)
    {
        var positions = new Dictionary<Guid, int>();
        foreach (var text in TextBlocksIn(book.Root))
        {
            positions[text.Key] = positions.Count;
        }

        return positions;
    }

    private static IEnumerable<TextBlock> TextBlocksIn(OutlineNode node)
    {
        foreach (var item in node.Items)
        {
            switch (item)
            {
                case TextBlock text:
                    yield return text;
                    break;
                case OutlineNode child:
                    foreach (var inner in TextBlocksIn(child))
                    {
                        yield return inner;
                    }

                    break;
            }
        }
    }
}
