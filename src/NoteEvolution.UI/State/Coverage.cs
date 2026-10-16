using NoteEvolution.AI.Understanding;
using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;

namespace NoteEvolution.UI.State;

/// <summary>
/// What the coverage needs of a note (a top-level block): its date, usage and placement check in the current book, and
/// whether no (further) check of it will come: the background gave it up, or there is no topic to offer it.
/// </summary>
public sealed record CoverageNote(Guid Key, DateOnly? Date, bool Used, IReadOnlyList<UsedInEntry> Usages, NotePlacement? Placement,
    bool NoCheckComing = false);

/// <summary>
/// The numbers of one section (heading, or the book's root): its own used and assigned notes (and how many of the assigned
/// ones are uncertain), and the same with all its sub-sections added (spec 6.1).
/// </summary>
public sealed record SectionCoverage(Guid SectionKey, int Used, int Assigned, int Uncertain, int TotalUsed, int TotalAssigned,
    int TotalUncertain, bool IsLeaf);

/// <summary>
/// The coverage of a book (spec 6.1): the numbers per section (by heading key, the root under <see cref="Guid.Empty"/>),
/// the used notes without a known place, the unused notes without a fitting place and those whose placement is
/// uncertain (both newest first), how many unused notes have a current placement check of how many there are, and how
/// many are settled: checked, or no check will come. It belongs to the book <see cref="BookLinkName"/>.
/// </summary>
public sealed record Coverage(IReadOnlyDictionary<Guid, SectionCoverage> Sections, int UsedWithoutPlace,
    IReadOnlyList<Guid> Homeless, IReadOnlyList<Guid> Uncertain, int Checked, int Unused, int Settled, string BookLinkName)
{
    /// <summary>
    /// Every unused note is settled (a current check, or none will come), so an empty section is not just empty for now.
    /// </summary>
    public bool Complete => Settled == Unused;

    /// <summary>Whether this is the coverage of <paramref name="book"/> (not of the book shown before a switch).</summary>
    public bool IsFor(Book? book) => string.Equals(book?.LinkName, BookLinkName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The section has no sub-sections and neither used nor assigned notes.</summary>
    public bool IsEmpty(Guid sectionKey) =>
        Sections.TryGetValue(sectionKey, out var section) && section is { IsLeaf: true, TotalUsed: 0, TotalAssigned: 0 };
}

/// <summary>Computes the <see cref="Coverage"/> of a book from its notes (spec 6.1); pure, so it may run in the background.</summary>
public static class CoverageCalculator
{
    public static Coverage Compute(Book book, IReadOnlyList<CoverageNote> notes)
    {
        // Used-in entries name a block by its id::, which may be a text block's or one of its descendants'.
        var sectionOfId = new Dictionary<Guid, OutlineNode>();
        foreach (var textBlock in TextBlocks(book.Root))
        {
            AddIds(sectionOfId, textBlock.Block, textBlock.Section);
        }

        var counts = new Dictionary<Guid, (int Used, int Assigned, int Uncertain)>();
        void Count(OutlineNode section, int used, int assigned, int uncertain)
        {
            var c = counts.GetValueOrDefault(section.Key);
            counts[section.Key] = (c.Used + used, c.Assigned + assigned, c.Uncertain + uncertain);
        }

        var usedWithoutPlace = 0;
        var homeless = new List<CoverageNote>();
        var uncertainNotes = new List<CoverageNote>();
        var checkedCount = 0;
        var settled = 0;
        var unused = 0;
        foreach (var note in notes)
        {
            if (note.Used)
            {
                // A note counts once in each section it was used in; entries for other books are left out.
                var places = new HashSet<OutlineNode>();
                var withoutPlace = false;
                foreach (var entry in note.Usages)
                {
                    if (!string.Equals(entry.PageName, book.LinkName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (entry.BookBlockId is { } id && sectionOfId.TryGetValue(id, out var section))
                    {
                        places.Add(section);
                    }
                    else
                    {
                        withoutPlace = true;
                    }
                }

                foreach (var section in places)
                {
                    Count(section, 1, 0, 0);
                }

                if (withoutPlace)
                {
                    usedWithoutPlace++;
                }

                continue;
            }

            unused++;
            if (note.Placement is not { } placement || SectionOf(book, placement.Verdict) is not { } placed)
            {
                settled += note.NoCheckComing ? 1 : 0;
                continue; // no check yet, or it names a place that is no longer in the book (spec 8)
            }

            var uncertain = PlacementCheck.IsUncertain(book, placement.Verdict);
            Count(placed, 0, 1, uncertain ? 1 : 0);
            if (uncertain)
            {
                uncertainNotes.Add(note);
            }

            if (placement.Verdict.Fit == PlacementFit.Poor)
            {
                homeless.Add(note);
            }

            if (!placement.Stale)
            {
                checkedCount++;
            }

            if (!placement.Stale || note.NoCheckComing)
            {
                settled++;
            }
        }

        var sections = new Dictionary<Guid, SectionCoverage>();
        AddSections(sections, counts, book.Root);
        return new Coverage(sections, usedWithoutPlace, NewestFirst(homeless), NewestFirst(uncertainNotes), checkedCount, unused,
            settled, book.LinkName);
    }

    /// <summary>The section the verdict names: the one of its text block if it names one; null if that is gone.</summary>
    private static OutlineNode? SectionOf(Book book, PlacementVerdict verdict)
    {
        if (verdict.AfterTextBlockKey is { } after)
        {
            return book.FindTextBlock(after)?.Section;
        }

        return book.FindNode(verdict.SectionKey);
    }

    private static void AddIds(Dictionary<Guid, OutlineNode> sectionOfId, Block block, OutlineNode section)
    {
        if (block.Id is { } id)
        {
            sectionOfId.TryAdd(id, section);
        }

        foreach (var child in block.Children)
        {
            AddIds(sectionOfId, child, section);
        }
    }

    private static IEnumerable<TextBlock> TextBlocks(OutlineNode node) =>
        node.TextBlocks.Concat(node.Children.SelectMany(TextBlocks));

    /// <summary>Adds the section and its sub-sections; returns its totals.</summary>
    private static SectionCoverage AddSections(Dictionary<Guid, SectionCoverage> sections,
        Dictionary<Guid, (int Used, int Assigned, int Uncertain)> counts, OutlineNode node)
    {
        var (used, assigned, uncertain) = counts.GetValueOrDefault(node.Key);
        var (totalUsed, totalAssigned, totalUncertain) = (used, assigned, uncertain);
        var leaf = true;
        foreach (var child in node.Children)
        {
            leaf = false;
            var sub = AddSections(sections, counts, child);
            totalUsed += sub.TotalUsed;
            totalAssigned += sub.TotalAssigned;
            totalUncertain += sub.TotalUncertain;
        }

        var section = new SectionCoverage(node.Key, used, assigned, uncertain, totalUsed, totalAssigned, totalUncertain, leaf);
        sections[node.Key] = section;
        return section;
    }

    private static IReadOnlyList<Guid> NewestFirst(List<CoverageNote> notes) =>
        [.. notes.OrderByDescending(note => note.Date ?? DateOnly.MinValue).Select(note => note.Key)];
}
