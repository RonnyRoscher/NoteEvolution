using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Books;

/// <summary>
/// Structure commands of the manuscript (new section after / below, indent, outdent, remove a heading, delete a
/// detail). Like <see cref="OutlineEditor"/>, they change <c>book.Page</c> in memory and find their blocks through
/// the <see cref="Book"/> view; the caller saves the page and reloads the <see cref="Book"/>. Every command validates
/// first; if it throws (<see cref="ArgumentException"/>, also for an unknown key), the page is unchanged.
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
    /// The key to reveal afterwards: the first moved item, else the element before the heading in the file, else
    /// <see cref="Guid.Empty"/>.
    /// </returns>
    /// <exception cref="ArgumentException">Unknown heading.</exception>
    public static Guid RemoveHeading(Book book, Guid headingKey)
    {
        var node = OutlineEditor.FindHeading(book, headingKey, nameof(headingKey));
        var heading = node.Block!;
        var page = book.Page;
        var children = heading.Children.ToList();
        var reveal = children.FirstOrDefault()?.Key
                     ?? page.AllBlocks().TakeWhile(block => block != heading).LastOrDefault()?.Key
                     ?? Guid.Empty;

        var index = IndexOf(page, heading);
        for (var i = 0; i < children.Count; i++)
        {
            page.MoveBlock(children[i], heading.Parent, index + i);
        }

        page.RemoveBlock(heading);
        foreach (var child in node.Children)
        {
            OutlineEditor.Relevel(child, node.Parent!.Level + 1);
        }

        return reveal;
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
}
