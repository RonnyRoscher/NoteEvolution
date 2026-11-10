using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;

namespace NoteEvolution.UI.State;

/// <summary>Why a note's stored placement is checked again (spec 4), or <see cref="None"/> when it is kept.</summary>
internal enum RecheckReason { None, NoteText, SectionGone, NewCandidate, Rebuilt, NoBasis }

/// <summary>
/// Decides whether a note's latest placement still holds for a changed request (spec 4): only a changed note text, a
/// chosen section that is gone, a new candidate that may beat the chosen one, or a rebuilt book check it again. Other
/// summaries, other or vanished places, a renamed section found by its key and another order of the sections do not.
/// The request is always compared with the basis of the last real check, not with the previous request. Pure.
/// </summary>
internal static class PlacementKeep
{
    private const string Separator = " › ";

    /// <summary>
    /// Whether <paramref name="latest"/> (the note's latest stored result) must be checked again for
    /// <paramref name="prompt"/> (today's request in <paramref name="book"/>), by the first rule that holds.
    /// </summary>
    public static RecheckReason Decide(PlacementVerdict latest, PlacementCheckPrompt prompt, Book book)
    {
        if (latest.Basis is not { } basis || prompt.Basis is not { } today)
        {
            return RecheckReason.NoBasis;
        }

        if (basis.NoteHash != today.NoteHash)
        {
            return RecheckReason.NoteText;
        }

        var section = book.FindNode(latest.SectionKey)
            ?? (latest.SectionPath is { } stored ? SectionPath.Find(book, stored) : null);
        if (section is null)
        {
            return RecheckReason.SectionGone;
        }

        // A chosen section found by its key may have been renamed (or a heading above it): the basis' paths are read
        // with today's names along its path, so the rename alone changes nothing.
        var chosen = SectionPath.Of(section);
        var renamed = Renames(latest.SectionPath, chosen);
        var offered = basis.Offered.Select(renamed).ToHashSet(StringComparer.Ordinal);
        var chapters = basis.TopChapters.Select(renamed).ToHashSet(StringComparer.Ordinal);

        // A path that is not ranked counts as last.
        var ranking = today.Ranking.ToList();
        int Rank(string path) => ranking.IndexOf(path) is >= 0 and var rank ? rank : int.MaxValue;
        var chosenRank = Rank(chosen);

        if (today.TopChapters.Any(chapter => !chapters.Contains(chapter))
            || today.Offered.Any(path => !offered.Contains(path) && Rank(path) < chosenRank))
        {
            return RecheckReason.NewCandidate;
        }

        return today.Offered.Intersect(offered, StringComparer.Ordinal).Count() * 2 < offered.Count
            ? RecheckReason.Rebuilt
            : RecheckReason.None;
    }

    /// <summary>
    /// Reads a basis path with today's names: when the chosen section's stored path <paramref name="before"/> and today's
    /// <paramref name="now"/> have the same depth, a path starting with one of the former's heads gets the latter's head.
    /// </summary>
    private static Func<string, string> Renames(string? before, string now)
    {
        if (before is null || before == now)
        {
            return path => path;
        }

        var old = before.Split(Separator);
        var current = now.Split(Separator);
        if (old.Length != current.Length)
        {
            return path => path; // moved, not renamed
        }

        return path =>
        {
            var parts = path.Split(Separator);
            var same = 0;
            while (same < parts.Length && same < old.Length && parts[same] == old[same])
            {
                same++;
            }

            return same == 0 ? path : string.Join(Separator, current.Take(same).Concat(parts.Skip(same)));
        };
    }
}
