namespace NoteEvolution.Core.Books;

/// <summary>An editor node to mark: a heading, a text block or a detail (paragraph) key.</summary>
/// <param name="Key"><see cref="Model.Block.Key"/> of the heading, the text block or the detail block.</param>
/// <param name="OwnTextOnly">Text block: only its own text, without details. Detail: only this paragraph, without deeper ones.</param>
public sealed record MarkedNode(Guid Key, bool OwnTextOnly);

/// <summary>The marked range of the manuscript: a level around the element at the cursor.</summary>
/// <param name="Head">The element that defines the range; null = the whole book (root).</param>
/// <param name="Level">-1, 0, 1, …</param>
/// <param name="Nodes">The nodes to mark, in document order.</param>
/// <param name="TextBlockKeys">The text blocks in the range (for the topic and the sources), in file order.</param>
/// <param name="SectionKey">The heading whose path names the topic: the head itself, or the head's section; Guid.Empty for the prologue or the whole book.</param>
/// <param name="MergeGroups">For every heading (or the root) in the range: its own text blocks in the range, in file order; only groups with ≥ 2 blocks.</param>
public sealed record RangeInfo(BookElement? Head, int Level, IReadOnlyList<MarkedNode> Nodes,
    IReadOnlyList<Guid> TextBlockKeys, Guid SectionKey, IReadOnlyList<IReadOnlyList<Guid>> MergeGroups);

/// <summary>
/// Computes the marked range around an element from the book tree (not from the <c>#</c> count). Level 0 is the element
/// with everything below it, level −1 the element without its sub-elements, level k &gt; 0 the k-th ancestor at level 0.
/// </summary>
public static class BookRanges
{
    /// <summary>The range of level <paramref name="level"/> around <paramref name="cursor"/>; level is clamped to [-1, MaxLevel].</summary>
    /// <exception cref="ArgumentException">The element is not in the book.</exception>
    public static RangeInfo Of(Book book, BookElement cursor, int level)
    {
        var ancestors = Ancestors(book, cursor);
        level = Math.Clamp(level, -1, ancestors.Count);
        var head = level <= 0 ? cursor : ancestors[level - 1];
        var ownOnly = level == -1;

        var nodes = new List<MarkedNode>();
        var textBlocks = new List<Guid>();
        var mergeGroups = new List<IReadOnlyList<Guid>>();
        switch (head?.Kind)
        {
            case null:
                AddSection(book.Root, true, nodes, textBlocks, mergeGroups);
                break;
            case ElementKind.Heading:
                AddSection(book.FindNode(head.Key)!, !ownOnly, nodes, textBlocks, mergeGroups);
                break;
            default:
                // A text block or a detail: the node itself; its text block is the only one in the range.
                nodes.Add(new MarkedNode(head.Key, ownOnly));
                textBlocks.Add(BookElements.TextBlockOf(book, head)!.Key);
                break;
        }

        var sectionKey = head is null ? Guid.Empty : BookElements.SectionOf(book, head);
        return new RangeInfo(head, level, nodes, textBlocks, sectionKey, mergeGroups);
    }

    /// <summary>The level whose head is the whole book (the number of ancestors of the cursor element up to the root).</summary>
    /// <exception cref="ArgumentException">The element is not in the book.</exception>
    public static int MaxLevel(Book book, BookElement cursor) => Ancestors(book, cursor).Count;

    /// <summary>
    /// The ancestors of <paramref name="element"/>, nearest first, ending with <c>null</c> for the root: for a detail its
    /// parent details, then its text block, then the headings; for a text block its headings; for a heading its parent headings.
    /// </summary>
    private static List<BookElement?> Ancestors(Book book, BookElement element)
    {
        var ancestors = new List<BookElement?>();
        OutlineNode section;
        switch (element.Kind)
        {
            case ElementKind.Heading:
                section = book.FindNode(element.Key) is { Block: not null } heading
                    ? heading.Parent!
                    : throw new ArgumentException("There is no such heading in the book.", nameof(element));
                break;
            case ElementKind.Detail:
                var owner = BookElements.TextBlockOf(book, element)!;
                var detail = owner.Paragraphs.Single(p => p.Block.Key == element.Key).Block;
                for (var parent = detail.Parent!; parent != owner.Block; parent = parent.Parent!)
                {
                    ancestors.Add(new BookElement(ElementKind.Detail, parent.Key));
                }

                ancestors.Add(new BookElement(ElementKind.TextBlock, owner.Key));
                section = owner.Section;
                break;
            default:
                section = BookElements.TextBlockOf(book, element)!.Section;
                break;
        }

        for (var node = section; node is not null; node = node.Parent)
        {
            ancestors.Add(node.Block is null ? null : new BookElement(ElementKind.Heading, node.Key));
        }

        return ancestors;
    }

    /// <summary>
    /// Adds a heading (none for the root) with its text blocks, whole, in file order, and with
    /// <paramref name="withSubsections"/> its sub-sections recursively; each section's own text blocks form a merge group.
    /// </summary>
    private static void AddSection(
        OutlineNode section, bool withSubsections, List<MarkedNode> nodes, List<Guid> textBlocks, List<IReadOnlyList<Guid>> mergeGroups)
    {
        if (section.Block is not null)
        {
            nodes.Add(new MarkedNode(section.Key, false));
        }

        // The section's own group comes before its sub-sections' groups, even if some of its blocks stand after them.
        List<Guid> ownBlocks = [.. section.TextBlocks.Select(textBlock => textBlock.Key)];
        if (ownBlocks.Count >= 2)
        {
            mergeGroups.Add(ownBlocks);
        }

        foreach (var item in section.Items)
        {
            switch (item)
            {
                case OutlineNode child when withSubsections:
                    AddSection(child, true, nodes, textBlocks, mergeGroups);
                    break;
                case TextBlock textBlock:
                    nodes.Add(new MarkedNode(textBlock.Key, false));
                    textBlocks.Add(textBlock.Key);
                    break;
            }
        }
    }
}
