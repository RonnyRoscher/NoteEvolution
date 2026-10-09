using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Books;

/// <summary>
/// Structure commands of the manuscript (new section after / below, indent, outdent, remove a heading, delete a
/// detail, put a range under a new heading). Like <see cref="OutlineEditor"/>, they change <c>book.Page</c> in memory
/// and find their blocks through the <see cref="Book"/> view; the caller saves the page and reloads the
/// <see cref="Book"/>. Every command validates first; if it throws (<see cref="ArgumentException"/>, also for an unknown key), the page is unchanged.
/// New headings are empty (<c>"#" * level + " "</c>), new text blocks and details are empty blocks.
/// </summary>
public static class ManuscriptEditor
{
    /// <summary>
    /// Adds a new empty element after <paramref name="current"/>: for a heading of level n a level-n heading after its
    /// whole section, for a text block a text block directly after it, for a detail a detail of the same depth after
    /// it and its deeper details.
    /// </summary>
    /// <returns>The <see cref="Block.Key"/> of the new element.</returns>
    /// <exception cref="ArgumentException">The element is not in the book.</exception>
    public static Guid InsertAfter(Book book, BookElement current)
    {
        var block = BlockOf(book, current);
        var content = current.Kind == ElementKind.Heading ? EmptyHeading(book.FindNode(current.Key)!.Level) : "";
        return Insert(book, block.Parent, IndexOf(book.Page, block) + 1, content);
    }

    /// <summary>
    /// Adds a new empty element as the first child of <paramref name="current"/>: for a heading of level n a level-(n+1)
    /// heading after its own text blocks and before its existing sub-sections, for a text block a detail of depth 1,
    /// for a detail a detail one level deeper.
    /// </summary>
    /// <returns>The <see cref="Block.Key"/> of the new element.</returns>
    /// <exception cref="ArgumentException">The element is not in the book, or it is a level-6 heading.</exception>
    public static Guid InsertChild(Book book, BookElement current)
    {
        var block = BlockOf(book, current);
        if (current.Kind != ElementKind.Heading)
        {
            return Insert(book, block, 0, "");
        }

        var node = book.FindNode(current.Key)!;
        if (node.Level >= OutlineEditor.MaxLevel)
        {
            throw new ArgumentException($"A heading below level {node.Level} would exceed level {OutlineEditor.MaxLevel}.", nameof(current));
        }

        var firstSubsection = node.Children.FirstOrDefault()?.Block;
        var index = firstSubsection is null ? block.Children.Count : IndexOf(book.Page, firstSubsection);
        return Insert(book, block, index, EmptyHeading(node.Level + 1));
    }

    /// <summary>Whether <see cref="InsertChild"/> is possible: <c>false</c> for a level-6 heading or an unknown element.</summary>
    public static bool CanInsertChild(Book book, BookElement current) =>
        BookElements.Find(book, current.Key) == current
        && (current.Kind != ElementKind.Heading || book.FindNode(current.Key)!.Level < OutlineEditor.MaxLevel);

    /// <summary>
    /// Moves the heading one level deeper, with its sub-sections: it becomes the last sub-section of the previous
    /// heading of the same level (<see cref="OutlineEditor.MoveSection"/>).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Unknown heading, no previous heading of the same level in the same section, or the subtree would get a heading
    /// deeper than level 6.
    /// </exception>
    public static void Indent(Book book, Guid headingKey)
    {
        var node = OutlineEditor.FindHeading(book, headingKey, nameof(headingKey));
        var previous = PreviousSameLevel(node)
                       ?? throw new ArgumentException("There is no previous heading of the same level.", nameof(headingKey));
        OutlineEditor.MoveSection(book, headingKey, previous.Key, previous.Children.Count());
    }

    /// <summary>
    /// Moves the heading one level higher, with its sub-sections: it is placed directly after the section of its
    /// former parent heading, and every heading of its subtree gets one <c>#</c> less.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown heading, or a level-1 heading.</exception>
    public static void Outdent(Book book, Guid headingKey)
    {
        var node = OutlineEditor.FindHeading(book, headingKey, nameof(headingKey));
        if (!CanOutdent(node))
        {
            throw new ArgumentException("A level-1 heading cannot be outdented.", nameof(headingKey));
        }

        var parent = node.Parent!;
        var grandparent = parent.Parent!;
        book.Page.MoveBlock(node.Block!, grandparent.Block, IndexOf(book.Page, parent.Block!) + 1);
        OutlineEditor.Relevel(node, grandparent.Level + 1);
    }

    /// <summary>Whether <see cref="Indent"/> is possible; <c>false</c> for an unknown key.</summary>
    public static bool CanIndent(Book book, Guid headingKey) =>
        book.FindNode(headingKey) is { Block: not null } node
        && PreviousSameLevel(node) is { } previous
        && previous.Level + OutlineEditor.Height(node) <= OutlineEditor.MaxLevel;

    /// <summary>Whether <see cref="Outdent"/> is possible; <c>false</c> for an unknown key.</summary>
    public static bool CanOutdent(Book book, Guid headingKey) =>
        book.FindNode(headingKey) is { Block: not null } node && CanOutdent(node);

    /// <summary>
    /// Removes the heading but keeps its content: its child blocks (text blocks and sub-sections) take its place in
    /// the surrounding section, in order, and every moved sub-heading gets the level of the surrounding section + 1
    /// (its own sub-headings the levels below).
    /// </summary>
    /// <returns>
    /// What was done, for a targeted undo (<see cref="RemovedHeading.RestoreInto"/>), with the key to reveal afterwards
    /// (<see cref="RemovedHeading.Reveal"/>): the first moved item, else the element before the heading in the file,
    /// else <see cref="Guid.Empty"/>.
    /// </returns>
    /// <exception cref="ArgumentException">Unknown heading.</exception>
    public static RemovedHeading RemoveHeading(Book book, Guid headingKey)
    {
        var node = OutlineEditor.FindHeading(book, headingKey, nameof(headingKey));
        var heading = node.Block!;
        var page = book.Page;
        var parent = heading.Parent;
        var children = heading.Children.ToList();
        var reveal = children.FirstOrDefault()?.Key
                     ?? page.AllBlocks().TakeWhile(block => block != heading).LastOrDefault()?.Key
                     ?? Guid.Empty;

        // Every moved block with its lines before, and each moved heading with its level before and after (as Relevel sets it).
        var moved = children.SelectMany(Subtree).Select(block => (Block: block, Before: block.Lines.ToList())).ToList();
        var levels = new Dictionary<Guid, (int Before, int After)>();
        foreach (var child in node.Children)
        {
            NewLevels(child, node.Parent!.Level + 1, levels);
        }

        var index = IndexOf(page, heading);
        for (var i = 0; i < children.Count; i++)
        {
            page.MoveBlock(children[i], parent, index + i);
        }

        page.RemoveBlock(heading);
        foreach (var child in node.Children)
        {
            OutlineEditor.Relevel(child, node.Parent!.Level + 1);
        }

        return new RemovedHeading(reveal, heading, parent?.Key, index, [.. children.Select(child => child.Key)], MovedBlocks(moved, levels));
    }

    /// <summary>
    /// Whether <see cref="Wrap"/> is possible: <c>false</c> for a detail, for no head (the whole book), for an unknown
    /// element and when a heading would get deeper than level 6.
    /// </summary>
    public static bool CanWrap(Book book, BookElement? head) =>
        head is not null && BookElements.Find(book, head.Key) == head && head.Kind switch
        {
            ElementKind.Heading => book.FindNode(head.Key) is { } node
                                   && node.Parent!.Level + 1 + OutlineEditor.Height(node) <= OutlineEditor.MaxLevel,
            ElementKind.TextBlock => book.FindTextBlock(head.Key)!.Section.Level + 1 <= OutlineEditor.MaxLevel,
            _ => false,
        };

    /// <summary>
    /// Puts the range of <paramref name="head"/> under a new empty heading N of the level of the head's section S + 1,
    /// placed at the head's index in S. A heading head becomes N's only child with its whole section, and every heading
    /// of its subtree gets the level below N (<see cref="OutlineEditor.Relevel"/>). A text block head and the own text
    /// blocks of S directly following it, up to S's next sub-section (or S's end), become N's children, in order; S's
    /// sub-sections and own text blocks after one of them stay where they are.
    /// </summary>
    /// <returns>What was done, for a targeted undo (<see cref="WrappedSection.RestoreInto"/>), with N's key.</returns>
    /// <exception cref="ArgumentException"><see cref="CanWrap"/> is <c>false</c>.</exception>
    public static WrappedSection Wrap(Book book, BookElement head)
    {
        if (!CanWrap(book, head))
        {
            throw new ArgumentException("The element cannot be put under a new heading.", nameof(head));
        }

        var page = book.Page;
        var node = head.Kind == ElementKind.Heading ? book.FindNode(head.Key)! : null;
        var section = node?.Parent ?? book.FindTextBlock(head.Key)!.Section;
        List<Block> tops = node is not null
            ? [node.Block!]
            : [.. section.Items
                .SkipWhile(item => !(item is TextBlock textBlock && textBlock.Key == head.Key))
                .TakeWhile(item => item is TextBlock)
                .Select(item => ((TextBlock)item).Block)];
        var indices = tops.Select(top => IndexOf(page, top)).ToList();

        // Every moved block with its lines before, and each moved heading with its level before and after (as Relevel sets it).
        var moved = tops.SelectMany(Subtree).Select(block => (Block: block, Before: block.Lines.ToList())).ToList();
        var levels = new Dictionary<Guid, (int Before, int After)>();
        if (node is not null)
        {
            NewLevels(node, section.Level + 2, levels);
        }

        var heading = Block.CreateDetached(EmptyHeading(section.Level + 1));
        page.InsertBlock(section.Block, indices[0], heading);
        for (var i = 0; i < tops.Count; i++)
        {
            page.MoveBlock(tops[i], heading, i);
        }

        if (node is not null)
        {
            OutlineEditor.Relevel(node, section.Level + 2);
        }

        return new WrappedSection(heading.Key, section.Block?.Key, indices, [.. tops.Select(top => top.Key)], MovedBlocks(moved, levels));
    }

    /// <summary>Deletes a detail with its deeper details.</summary>
    /// <exception cref="ArgumentException">The key is not that of a detail.</exception>
    public static void DeleteDetail(Book book, Guid detailKey)
    {
        var block = BlockOf(book, new BookElement(ElementKind.Detail, detailKey));
        book.Page.RemoveBlock(block);
    }

    /// <summary>The element's block.</summary>
    /// <exception cref="ArgumentException">No element of this kind has the key.</exception>
    private static Block BlockOf(Book book, BookElement element)
    {
        if (BookElements.Find(book, element.Key) != element)
        {
            throw new ArgumentException($"There is no such {element.Kind} in the book.", nameof(element));
        }

        return element.Kind switch
        {
            ElementKind.Heading => book.FindNode(element.Key)!.Block!,
            ElementKind.TextBlock => book.FindTextBlock(element.Key)!.Block,
            _ => BookElements.TextBlockOf(book, element)!.Paragraphs.Single(p => p.Block.Key == element.Key).Block,
        };
    }

    private static Guid Insert(Book book, Block? parent, int index, string content)
    {
        var block = Block.CreateDetached(content);
        book.Page.InsertBlock(parent, index, block);
        return block.Key;
    }

    private static string EmptyHeading(int level) => HeadingText.WithTitle("", level, "");

    /// <summary>The index of <paramref name="block"/> among its siblings (for a root block: among the page's roots).</summary>
    private static int IndexOf(Page page, Block block)
    {
        var siblings = block.Parent?.Children ?? page.Roots;
        var index = 0;
        while (siblings[index] != block)
        {
            index++;
        }

        return index;
    }

    /// <summary>The last heading of the same level before <paramref name="node"/> in its section, or <c>null</c>.</summary>
    private static OutlineNode? PreviousSameLevel(OutlineNode node) =>
        node.Parent!.Children.TakeWhile(sibling => sibling != node).LastOrDefault(sibling => sibling.Level == node.Level);

    private static bool CanOutdent(OutlineNode node) => node.Level > 1 && node.Parent is { Block: not null };

    /// <summary>The block and all blocks below it, depth-first in file order.</summary>
    private static IEnumerable<Block> Subtree(Block block) => block.Children.SelectMany(Subtree).Prepend(block);

    /// <summary>Records the level of <paramref name="node"/> and its sub-headings before and after <c>Relevel(node, level)</c>.</summary>
    private static void NewLevels(OutlineNode node, int level, Dictionary<Guid, (int Before, int After)> levels)
    {
        levels[node.Key] = (node.Level, level);
        foreach (var child in node.Children)
        {
            NewLevels(child, level + 1, levels);
        }
    }

    /// <summary>The moved blocks for an undo, each with its lines before and now, its levels and its parent now.</summary>
    private static List<RemovedHeading.MovedBlock> MovedBlocks(
        IEnumerable<(Block Block, List<RawLine> Before)> moved, Dictionary<Guid, (int Before, int After)> levels) =>
        [.. moved.Select(m => new RemovedHeading.MovedBlock(
            m.Block.Key,
            m.Before,
            [.. m.Block.Lines],
            levels.TryGetValue(m.Block.Key, out var level) ? level : null,
            m.Block.Parent?.Key))];
}
