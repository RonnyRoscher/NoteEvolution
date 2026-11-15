using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Books;

/// <summary>
/// Outline operations of a book: add, rename and move sections (heading blocks). They change <c>book.Page</c> in
/// memory and find their blocks by <see cref="Block.Key"/> through the <see cref="Book"/> view; they do not save.
/// The caller saves the page (e.g. via <c>IPageWriter</c>) and reloads the <see cref="Book"/>, because the view
/// is stale after an operation. Every operation validates its arguments first; if it throws, the page is unchanged.
/// <see cref="Guid.Empty"/> stands for the root (level 0), the parent of the top-level headings. The appendix
/// sections (<see cref="OutlineNode.AppendixKind"/>) are no section of the book's text: they are neither changed nor
/// a parent here, and new or moved top-level headings go before the first of them.
/// </summary>
public static class OutlineEditor
{
    /// <summary>The deepest heading level (H6).</summary>
    public const int MaxLevel = 6;

    /// <summary>
    /// Adds a heading <c>"#" * (parent level + 1) + " " + title</c> as the last child of the parent
    /// (for the root: the last root block before the appendix).
    /// </summary>
    /// <returns>The <see cref="Block.Key"/> of the new heading.</returns>
    /// <exception cref="ArgumentException">
    /// Unknown parent or one in the appendix, a title that is blank or spans several lines, or a parent that is already
    /// level 6.
    /// </exception>
    public static Guid AddHeading(Book book, Guid parentKey, string title)
    {
        var parent = FindSection(book, parentKey, nameof(parentKey));
        title = CheckTitle(title);
        if (parent.Level + 1 > MaxLevel)
        {
            throw new ArgumentException($"A heading below level {parent.Level} would exceed level {MaxLevel}.", nameof(parentKey));
        }

        var heading = Block.CreateDetached(HeadingText.WithTitle("", parent.Level + 1, title));
        var index = ChildBlocks(book, parent).Count;
        book.Page.InsertBlock(parent.Block, parent.Block is null ? book.BeforeAppendix(index) : index, heading);
        return heading.Key;
    }

    /// <summary>
    /// Replaces the title of a heading; its <c>#</c> prefix, further content lines and properties
    /// (<c>collapsed::</c>, <c>id::</c>) stay.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown heading (or the root), one in the appendix, or a blank / multi-line title.</exception>
    public static void Rename(Book book, Guid nodeKey, string title)
    {
        var node = FindHeading(book, nodeKey, nameof(nodeKey));
        title = CheckTitle(title);
        if (title != node.Title)
        {
            node.Block!.SetContent(HeadingText.WithTitle(node.Block.Content, node.Level, title));
        }
    }

    /// <summary>
    /// Moves a heading with everything below it and its <see cref="ReadTail"/> to <paramref name="index"/> among the
    /// headings (<see cref="OutlineNode"/> children, without the moved node itself) of the new parent. The tail is first
    /// nested under the heading in its order (<see cref="NestTail"/>), so the file is nested as the book reads. Other
    /// text blocks of the new parent keep their place in the file. Every heading in the moved subtree gets the level
    /// <c>new parent level + 1 + depth below the moved heading</c>; only the <c>#</c> prefix changes. At the root, an
    /// index after the last section of the book's text means before the first appendix section. A target that is the
    /// section's own place (directly before or after its tail headings in its parent) changes nothing, the tail included,
    /// if every heading of the section already has the level the move gives it; otherwise the section stays at its place
    /// and is moved there as anywhere else (tail nested, levels set).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Unknown heading or parent, either in the appendix, the new parent is the node itself or inside its subtree or its
    /// tail, the index lies between two headings of its tail, or the subtree with its nested tail would get a heading
    /// deeper than level 6.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not within 0..number of headings.</exception>
    public static void MoveSection(Book book, Guid nodeKey, Guid newParentKey, int index)
    {
        var node = FindHeading(book, nodeKey, nameof(nodeKey));
        var newParent = FindSection(book, newParentKey, nameof(newParentKey));
        var tail = ReadTail(book, nodeKey);
        if (IsInSection(newParent.Block, node.Block!, tail))
        {
            throw new ArgumentException("A section cannot be moved into its own subtree.", nameof(newParentKey));
        }

        var headings = newParent.Children.Where(child => child != node).ToList();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, headings.Count);
        if (headings.FindIndex(heading => heading.AppendixKind != AppendixKind.None) is >= 0 and var firstAppendix)
        {
            index = Math.Min(index, firstAppendix);
        }

        if (newParent == node.Parent)
        {
            // Among the headings without the node, its own place is from the first to after the last tail heading.
            var own = newParent.Children.TakeWhile(child => child != node).Count();
            var tailHeadings = tail.Count(block => book.FindNode(block.Key) is not null);
            if (index > own && index < own + tailHeadings)
            {
                throw new ArgumentException("A section cannot be moved into its own section.", nameof(index));
            }

            if (index == own || index == own + tailHeadings)
            {
                if (HasLevels(node, newParent.Level + 1))
                {
                    return;
                }

                // Its own place at another level: stay there (after the tail headings, which are nested) and relevel.
                index = own + tailHeadings;
            }
        }

        var deepest = newParent.Level + HeightWithTail(book, node, tail);
        if (deepest > MaxLevel)
        {
            throw new ArgumentException($"The moved section would reach level {deepest}; the maximum is {MaxLevel}.", nameof(newParentKey));
        }

        // The heading the section goes before (null: the end); it is no tail block, so nesting the tail keeps it.
        var before = index < headings.Count ? headings[index].Block : null;
        NestTail(book, nodeKey, tail);

        // Translate the heading into the index among all child blocks (text blocks stay where they are).
        var siblings = ChildBlocks(book, newParent).Where(block => block != node.Block).ToList();
        var blockIndex = before is null ? siblings.Count : siblings.IndexOf(before);

        book.Page.MoveBlock(node.Block!, newParent.Block, blockIndex);
        Relevel(Book.Load(book.Page).FindNode(nodeKey)!, newParent.Level + 1);
    }

    /// <summary>
    /// Nests the <paramref name="tail"/> of the heading <paramref name="nodeKey"/> under it, in order: a text block becomes
    /// the last child of the deepest open heading at the section's end (the chain of last children that are headings);
    /// a heading becomes the heading's last child and is then placed as "Gliederung einordnen" places it
    /// (<see cref="ManuscriptEditor.Place"/>: into the heading before it while that one's level is smaller).
    /// </summary>
    private static void NestTail(Book book, Guid nodeKey, IReadOnlyList<Block> tail)
    {
        var page = book.Page;
        foreach (var block in tail)
        {
            var view = Book.Load(page);
            var section = view.FindNode(nodeKey)!;
            if (view.FindNode(block.Key) is { } heading)
            {
                page.MoveBlock(block, section.Block, section.Block!.Children.Count);
                ManuscriptEditor.Place(view, heading);
                continue;
            }

            while (section.Items.LastOrDefault() is OutlineNode last)
            {
                section = last;
            }

            page.MoveBlock(block, section.Block, section.Block!.Children.Count);
        }
    }

    /// <summary>
    /// <see cref="Height"/> of the section after <see cref="NestTail"/>, computed without changing the page: each tail
    /// heading goes below the open heading (the chain of last children that are headings) where
    /// <see cref="ManuscriptEditor.Place"/> stops, and then ends the chain with its own last children; a text block leaves
    /// the chain as it is.
    /// </summary>
    internal static int HeightWithTail(Book book, OutlineNode node, IReadOnlyList<Block> tail)
    {
        var open = new List<OutlineNode>();
        AddOpenChain(open, node);
        var height = Height(node);
        foreach (var block in tail)
        {
            if (book.FindNode(block.Key) is not { } heading)
            {
                continue;
            }

            var parentIndex = 0;
            while (parentIndex + 1 < open.Count && open[parentIndex + 1].Level < heading.Level)
            {
                parentIndex++;
            }

            open.RemoveRange(parentIndex + 1, open.Count - parentIndex - 1);
            AddOpenChain(open, heading);
            height = Math.Max(height, parentIndex + 1 + Height(heading));
        }

        return height;
    }

    /// <summary>Whether <see cref="Relevel"/> with <paramref name="level"/> would rewrite nothing in the subtree.</summary>
    private static bool HasLevels(OutlineNode node, int level) =>
        node.Level == level && node.Children.All(child => HasLevels(child, level + 1));

    /// <summary>Adds <paramref name="node"/> and the chain of its last items that are headings.</summary>
    private static void AddOpenChain(List<OutlineNode> open, OutlineNode node)
    {
        for (OutlineNode? current = node; current is not null; current = current.Items.LastOrDefault() as OutlineNode)
        {
            open.Add(current);
        }
    }

    /// <summary>Whether <paramref name="block"/> is the heading, lies below it or in its tail (or below a tail block).</summary>
    private static bool IsInSection(Block? block, Block heading, IReadOnlyList<Block> tail)
    {
        for (var ancestor = block; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor == heading || tail.Contains(ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The blocks after the heading's block under the same parent that are read as part of its section: text blocks and
    /// headings of a deeper level, up to (not including) the next heading of the same or a higher level, an appendix
    /// section or the end of the parent. Empty for a heading without such blocks.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown heading (or the root), or one in the appendix.</exception>
    public static IReadOnlyList<Block> ReadTail(Book book, Guid headingKey)
    {
        var node = FindHeading(book, headingKey, nameof(headingKey));
        var siblings = node.Block!.Parent?.Children ?? book.Page.Roots;
        return
        [
            .. siblings.SkipWhile(block => block != node.Block).Skip(1)
                .TakeWhile(block => book.FindNode(block.Key) is not { } heading
                                    || (heading.Level > node.Level && heading.AppendixKind == AppendixKind.None)),
        ];
    }

    /// <summary>Moves the section to the same level as <paramref name="anchorSectionKey"/>, directly after it
    /// (<see cref="Guid.Empty"/>: first section of the top level).</summary>
    /// <remarks>
    /// "After it" is after the anchor's whole section in reading order: its sub-sections and its
    /// <see cref="ReadTail"/>, which stays where it is.
    /// </remarks>
    /// <exception cref="ArgumentException">As MoveSection; also if the anchor is the node or inside its subtree.</exception>
    public static void MoveSectionAfter(Book book, Guid nodeKey, Guid anchorSectionKey)
    {
        var node = FindHeading(book, nodeKey, nameof(nodeKey));
        if (anchorSectionKey == Guid.Empty)
        {
            MoveSection(book, nodeKey, Guid.Empty, 0);
            return;
        }

        var anchor = FindHeading(book, anchorSectionKey, nameof(anchorSectionKey));
        if (IsInSection(anchor.Block, node.Block!, ReadTail(book, nodeKey)))
        {
            throw new ArgumentException("A section cannot be moved after a part of itself.", nameof(anchorSectionKey));
        }

        var parent = anchor.Parent!;
        var headings = parent.Children.Where(child => child != node).ToList();
        var anchorTailHeadings = ReadTail(book, anchorSectionKey)
            .Count(block => block != node.Block && book.FindNode(block.Key) is not null);
        MoveSection(book, nodeKey, parent.Key, headings.IndexOf(anchor) + 1 + anchorTailHeadings);
    }

    /// <summary>The section (or the root) <paramref name="key"/> of the book's text.</summary>
    /// <exception cref="ArgumentException">Unknown key, or a heading in the appendix.</exception>
    private static OutlineNode FindSection(Book book, Guid key, string paramName)
    {
        var node = book.FindNode(key) ?? throw new ArgumentException("There is no such heading in the book.", paramName);
        return book.IsInAppendix(key)
            ? throw new ArgumentException("The appendix is not changed by outline commands.", paramName)
            : node;
    }

    internal static OutlineNode FindHeading(Book book, Guid key, string paramName)
    {
        var node = FindSection(book, key, paramName);
        return node.Block is null ? throw new ArgumentException("The root is not a heading.", paramName) : node;
    }

    private static List<Block> ChildBlocks(Book book, OutlineNode section) => [.. section.Block?.Children ?? book.Page.Roots];

    private static string CheckTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (title.Contains('\n') || title.Contains('\r'))
        {
            throw new ArgumentException("A heading title must be a single line.", nameof(title));
        }

        return string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("A heading title must not be empty.", nameof(title))
            : title.Trim();
    }

    /// <summary>Number of heading levels in the subtree including the node itself.</summary>
    internal static int Height(OutlineNode node) => 1 + node.Children.Select(Height).DefaultIfEmpty(0).Max();

    /// <summary>Gives <paramref name="node"/> the level <paramref name="level"/> and its sub-headings the levels below.</summary>
    internal static void Relevel(OutlineNode node, int level)
    {
        if (node.Level != level)
        {
            node.Block!.SetContent(HeadingText.WithLevel(node.Block.Content, node.Level, level));
        }

        foreach (var child in node.Children)
        {
            Relevel(child, level + 1);
        }
    }
}
