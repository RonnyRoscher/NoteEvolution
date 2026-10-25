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
    /// Moves a heading with everything below it to <paramref name="index"/> among the headings
    /// (<see cref="OutlineNode"/> children, without the moved node itself) of the new parent. Text blocks of the
    /// new parent keep their place in the file. Every heading in the moved subtree gets the level
    /// <c>new parent level + 1 + depth below the moved heading</c>; only the <c>#</c> prefix changes. At the root, an
    /// index after the last section of the book's text means before the first appendix section.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Unknown heading or parent, either in the appendix, the new parent is the node itself or inside its subtree, or
    /// the subtree would get a heading deeper than level 6.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not within 0..number of headings.</exception>
    public static void MoveSection(Book book, Guid nodeKey, Guid newParentKey, int index)
    {
        var node = FindHeading(book, nodeKey, nameof(nodeKey));
        var newParent = FindSection(book, newParentKey, nameof(newParentKey));
        for (var ancestor = newParent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor == node)
            {
                throw new ArgumentException("A section cannot be moved into its own subtree.", nameof(newParentKey));
            }
        }

        var headings = newParent.Children.Where(child => child != node).ToList();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, headings.Count);
        if (headings.FindIndex(heading => heading.AppendixKind != AppendixKind.None) is >= 0 and var firstAppendix)
        {
            index = Math.Min(index, firstAppendix);
        }

        var deepest = newParent.Level + Height(node);
        if (deepest > MaxLevel)
        {
            throw new ArgumentException($"The moved section would reach level {deepest}; the maximum is {MaxLevel}.", nameof(newParentKey));
        }

        // Translate the heading index into the index among all child blocks (text blocks stay where they are).
        var siblings = ChildBlocks(book, newParent).Where(block => block != node.Block).ToList();
        var blockIndex = index < headings.Count ? siblings.IndexOf(headings[index].Block!) : siblings.Count;

        book.Page.MoveBlock(node.Block!, newParent.Block, blockIndex);
        Relevel(node, newParent.Level + 1);
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
