using NoteEvolution.Core.Books;
using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;
using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Assistants;

/// <summary>
/// What <see cref="SubPointLinker.Find"/> found: the matches to link, and per book (with at least one match or unmatched
/// detail) how many details are linked and how many stay without a unique match.
/// </summary>
public sealed record SubPointPlan(
    IReadOnlyList<SubPointMatch> Matches, IReadOnlyList<(string BookLinkName, int Linked, int Unmatched)> PerBook)
{
    /// <summary>The details of all books that stay without a unique match.</summary>
    public int Unmatched { get; } = PerBook.Sum(book => book.Unmatched);
}

/// <summary>
/// Assistant „Verknüpfungen der Unterpunkte nachtragen“: older adoptions linked only the top block to its note block;
/// the details copied from its sub-points have no <c>source::</c>, and the sub-points no <c>used-in::</c> of their own
/// (they inherit the main point's usage). This finds the sub-point each such detail was copied from and links the two.
/// </summary>
public sealed class SubPointLinker(IVault vault, ILinkService links)
{
    /// <summary>
    /// Goes over every writable book of the vault and every text block in it. Each detail D without <c>source::</c> of
    /// its own below a linked block B (the text block or a detail with <c>source::</c>) is compared with every sub-point
    /// (any depth) of each source note of B (and of further linked blocks between D and the text block): D matches a
    /// sub-point S when D's text equals S's text, both trimmed (an adoption copies a sub-point's text as it is; only the
    /// adopted note block itself loses its <c>#notiz</c> tag). D is matched only if exactly one sub-point of all those
    /// notes has its text, and S only if no other detail of the same text block matches it as well; every other such
    /// detail counts as unmatched, as does one whose sub-point is on a read-only note page and has no <c>id::</c> (the page
    /// never gets the <c>id::</c> the detail's <c>source::</c> would point to). A detail with an <c>id::</c> but no
    /// <c>source::</c> is no candidate at all: its link was loosened on purpose, or a new adoption never linked it.
    /// </summary>
    public SubPointPlan Find()
    {
        var matches = new List<SubPointMatch>();
        var perBook = new List<(string BookLinkName, int Linked, int Unmatched)>();
        foreach (var book in vault.Books.Where(book => !book.Page.IsReadOnly))
        {
            var linked = 0;
            var unmatched = 0;
            foreach (var textBlock in TextBlocksOf(book))
            {
                var found = new List<(Block Detail, Block SubPoint)>();
                foreach (var detail in textBlock.Paragraphs.Select(paragraph => paragraph.Block).Where(IsCandidate))
                {
                    var sources = SourcesAbove(detail, textBlock.Block);
                    if (sources.Count == 0)
                    {
                        // Below no linked block: nothing was adopted here.
                        continue;
                    }

                    var candidates = sources
                        .Select(NoteOf)
                        .OfType<Block>()
                        .SelectMany(SubPointsOf)
                        .Distinct()
                        .Where(subPoint => SameText(subPoint, detail))
                        .Take(2)
                        .ToList();
                    if (candidates.Count == 1 && CanBeLinked(candidates[0]))
                    {
                        found.Add((detail, candidates[0]));
                    }
                    else
                    {
                        unmatched++;
                    }
                }

                foreach (var bySubPoint in found.GroupBy(pair => pair.SubPoint))
                {
                    if (bySubPoint.Count() == 1)
                    {
                        var (detail, subPoint) = bySubPoint.Single();
                        matches.Add(new SubPointMatch(book, detail.Key, subPoint.Key));
                        linked++;
                    }
                    else
                    {
                        unmatched += bySubPoint.Count();
                    }
                }
            }

            if (linked > 0 || unmatched > 0)
            {
                perBook.Add((book.LinkName, linked, unmatched));
            }
        }

        return new SubPointPlan(matches, perBook);
    }

    /// <summary>Links the details of <paramref name="plan"/> to their sub-points (<see cref="ILinkService.LinkSubPoints"/>).</summary>
    public void Apply(SubPointPlan plan) => links.LinkSubPoints(plan.Matches);

    /// <summary>The text blocks of the book in file order, those of the appendix sections included.</summary>
    private static IEnumerable<TextBlock> TextBlocksOf(Book book) =>
        book.Root.AllTextBlocks()
            .Concat(book.Root.Children.Where(node => node.AppendixKind != AppendixKind.None).SelectMany(node => node.AllTextBlocks()));

    /// <summary>
    /// The sources of the linked blocks from the detail's parent up to the text block, each once (a missing note
    /// included: its detail then counts as unmatched).
    /// </summary>
    private static List<Guid> SourcesAbove(Block detail, Block textBlock)
    {
        var sources = new List<Guid>();
        for (var block = detail.Parent; block is not null; block = block.Parent)
        {
            sources.AddRange(SourceValue.Parse(block.GetProperty("source") ?? ""));
            if (block == textBlock)
            {
                break;
            }
        }

        return [.. sources.Distinct()];
    }

    /// <summary>The note block with this id, or <c>null</c> if there is none (or it is a book block).</summary>
    private Block? NoteOf(Guid id) => vault.FindBlockById(id) is { } found && !Book.IsBook(found.Page) ? found.Block : null;

    private static IEnumerable<Block> SubPointsOf(Block note) => note.Children.SelectMany(SubtreeOf);

    private static IEnumerable<Block> SubtreeOf(Block block) => block.Children.SelectMany(SubtreeOf).Prepend(block);

    private static bool HasSource(Block block) => SourceValue.Parse(block.GetProperty("source") ?? "").Count > 0;

    /// <summary>A detail an older adoption copied and left unlinked: no <c>source::</c> and no <c>id::</c>.</summary>
    private static bool IsCandidate(Block detail) => !HasSource(detail) && detail.Id is null;

    /// <summary>The sub-point has an <c>id::</c>, or can get one: its page is writable.</summary>
    private bool CanBeLinked(Block subPoint) =>
        subPoint.Id is not null || vault.FindBlockByKey(subPoint.Key) is { Page.IsReadOnly: false };

    private static bool SameText(Block subPoint, Block detail) =>
        string.Equals(subPoint.Content.Trim(), detail.Content.Trim(), StringComparison.Ordinal);
}
