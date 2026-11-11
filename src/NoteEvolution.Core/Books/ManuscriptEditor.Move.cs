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
