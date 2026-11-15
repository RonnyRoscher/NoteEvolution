using NoteEvolution.Core.Links;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Books;

public static partial class ManuscriptEditor
{
    /// <summary>
    /// Moves the text blocks <paramref name="textBlockKeys"/> with their details, in the given order, to
    /// <paramref name="position"/> (as a note is placed when it is dropped there, see <see cref="InsertPosition"/>). Their
    /// lines move unchanged apart from the indentation: ids, sources and dates stay, nothing is stamped, so the notes'
    /// usages still point at them. A position after one of the moved blocks, or the place they already have, changes
    /// nothing.
    /// </summary>
    /// <returns>Whether the page changed.</returns>
    /// <exception cref="ArgumentException">
    /// No keys, a key twice, a key of no text block of the manuscript (a heading, a detail, the appendix), or a position
    /// that is unknown or in the appendix; nothing is changed.
    /// </exception>
    public static bool MoveTextBlocks(Book book, IReadOnlyList<Guid> textBlockKeys, InsertPosition position)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(textBlockKeys);
        ArgumentNullException.ThrowIfNull(position);
        if (textBlockKeys.Count == 0 || textBlockKeys.Distinct().Count() != textBlockKeys.Count)
        {
            throw new ArgumentException("Move at least one text block, each once.", nameof(textBlockKeys));
        }

        var blocks = textBlockKeys
            .Select(key => !book.IsInAppendix(key) && book.FindTextBlock(key) is { } text
                ? text.Block
                : throw new ArgumentException("No text block of the manuscript has this key.", nameof(textBlockKeys)))
            .ToList();
        if (position is InsertPosition.After after && textBlockKeys.Contains(after.TextBlockKey))
        {
            return false;
        }

        var (parent, index) = LinkService.Resolve(book, position);
        var page = book.Page;
        var siblings = parent?.Children ?? page.Roots;

        // The block the moved ones follow, among those that stay (none: the parent's start).
        var anchor = siblings.Take(index).LastOrDefault(block => !blocks.Contains(block));
        if (AlreadyThere(siblings, anchor, blocks))
        {
            return false;
        }

        foreach (var block in blocks)
        {
            var others = (parent?.Children ?? page.Roots).Where(sibling => sibling != block).ToList();
            page.MoveBlock(block, parent, anchor is null ? 0 : others.IndexOf(anchor) + 1);
            anchor = block;
        }

        return true;
    }

    /// <summary>
    /// Moves the detail <paramref name="detailKey"/> with all details below it to <paramref name="target"/> (spec 5.3);
    /// the deeper details keep their distance to it. To <see cref="DetailTarget.FirstOf"/> or
    /// <see cref="DetailTarget.After"/> only the place changes: its lines stay as they are apart from the indentation
    /// (<c>id::</c>, <c>source::</c>, status), nothing is stamped. The detail's own text block is a
    /// <see cref="DetailTarget.FirstOf"/> target as well: a later sub-point moves to the front, the first one is already
    /// there. To <see cref="DetailTarget.SectionStart"/> or <see cref="DetailTarget.SectionEnd"/> it becomes a text block
    /// (of any depth) as <see cref="OutdentDetail"/> makes one (<see cref="BecomeTextBlock"/>), its deeper details its
    /// details; the details after it stay in its old text block.
    /// </summary>
    /// <returns>Whether the page changed: a target that is the place the detail already has changes nothing.</returns>
    /// <exception cref="ArgumentException">
    /// Unknown detail or target, one in the appendix, or a target that is the detail itself or one below it; nothing is
    /// changed.
    /// </exception>
    /// <exception cref="InvalidOperationException">The page is read-only; nothing is changed.</exception>
    public static bool MoveDetail(Book book, Guid detailKey, DetailTarget target, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(target);
        var element = BookElements.Find(book, detailKey) is { Kind: ElementKind.Detail } found
            ? found
            : throw new ArgumentException("No detail of the manuscript has this key.", nameof(detailKey));
        var detail = BlockOf(book, element);
        var textBlock = BookElements.TextBlockOf(book, element)!.Block;
        var page = book.Page;

        switch (target)
        {
            case DetailTarget.FirstOf first:
            {
                var receiving = !book.IsInAppendix(first.TextBlockKey) && book.FindTextBlock(first.TextBlockKey) is { } text
                    ? text.Block
                    : throw new ArgumentException("No text block of the manuscript has this key.", nameof(target));
                return MoveAmongDetails(page, detail, receiving, 0);
            }

            case DetailTarget.After after:
            {
                var anchor = BookElements.Find(book, after.DetailKey) is { Kind: ElementKind.Detail } detailElement
                    ? BlockOf(book, detailElement)
                    : throw new ArgumentException("No detail of the manuscript has this key.", nameof(target));
                for (var block = anchor; block is not null; block = block.Parent)
                {
                    if (block == detail)
                    {
                        throw new ArgumentException("A detail is not moved after itself or a detail below it.", nameof(target));
                    }
                }

                var parent = anchor.Parent!;
                var others = parent.Children.Where(child => child != detail).ToList();
                return MoveAmongDetails(page, detail, parent, others.IndexOf(anchor) + 1);
            }

            case DetailTarget.SectionStart start:
                return MoveToSection(book, detail, textBlock, new InsertPosition.SectionStart(start.SectionKey), now);

            case DetailTarget.SectionEnd end:
                return MoveToSection(book, detail, textBlock, new InsertPosition.SectionEnd(end.SectionKey), now);

            default:
                throw new ArgumentException("Unknown target.", nameof(target));
        }
    }

    /// <summary>Puts <paramref name="detail"/> of <paramref name="textBlock"/> at <paramref name="position"/> as a text block of its own.</summary>
    private static bool MoveToSection(Book book, Block detail, Block textBlock, InsertPosition position, DateTimeOffset now)
    {
        // Resolved first: an unknown section or one of the appendix is refused before anything changes.
        var (parent, index) = LinkService.Resolve(book, position);
        EnsureWritable(book.Page);
        book.Page.MoveBlock(detail, parent, index);
        BecomeTextBlock(detail, textBlock, now);
        return true;
    }

    /// <summary>
    /// Moves <paramref name="detail"/> to <paramref name="index"/> among the details of <paramref name="parent"/> other
    /// than itself; its place already: nothing changes.
    /// </summary>
    private static bool MoveAmongDetails(Page page, Block detail, Block parent, int index)
    {
        if (detail.Parent == parent && IndexIn(parent.Children, detail) == index)
        {
            return false;
        }

        EnsureWritable(page);
        page.MoveBlock(detail, parent, index);
        return true;
    }

    /// <summary>
    /// Treats <paramref name="detail"/>, which stands as a text block now, as one made from a detail of
    /// <paramref name="formerTextBlock"/> (Einordnen spec 4): a first line that would read as a heading is escaped, so it
    /// stays text; it gets both dates (<c>updated::</c> only if it has <c>created::</c>), the former text block
    /// <c>updated::</c>. Everything else stays: its own <c>id::</c>, <c>source::</c> and <c>#notiz</c>, and the former
    /// text block's <c>source::</c> with that one.
    /// </summary>
    private static void BecomeTextBlock(Block detail, Block formerTextBlock, DateTimeOffset now)
    {
        if (HeadingText.AsTextBlockContent(detail.Content, shownUnescaped: true) is var escaped && escaped != detail.Content)
        {
            detail.SetContent(escaped);
        }

        StampAsTextBlock(detail, now);
        BlockDates.StampChanged(formerTextBlock, now);
    }

    private static void EnsureWritable(Page page)
    {
        if (page.IsReadOnly)
        {
            throw new InvalidOperationException("The page is read-only.");
        }
    }

    /// <summary>Whether <paramref name="blocks"/> already follow <paramref name="anchor"/> (none: the start) in this order.</summary>
    private static bool AlreadyThere(IReadOnlyList<Block> siblings, Block? anchor, List<Block> blocks)
    {
        var start = anchor is null ? 0 : IndexIn(siblings, anchor) + 1;
        if (start + blocks.Count > siblings.Count)
        {
            return false;
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            if (siblings[start + i] != blocks[i])
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexIn(IReadOnlyList<Block> siblings, Block block)
    {
        for (var i = 0; i < siblings.Count; i++)
        {
            if (siblings[i] == block)
            {
                return i;
            }
        }

        return -1;
    }
}
