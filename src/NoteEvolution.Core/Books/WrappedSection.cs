using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Books;

/// <summary>
/// What <see cref="ManuscriptEditor.Wrap"/> did, so that it can be undone even after later edits of the book (as
/// <see cref="RemovedHeading"/>): the new heading N, the section it was put in, and the blocks moved below it (by key)
/// with their former indices, their lines and heading levels before and after the command and their parents after it.
/// <see cref="RestoreInto"/> removes N and puts the moved blocks back; the caller saves the page afterwards.
/// </summary>
public sealed class WrappedSection
{
    private readonly Guid? _sectionKey;
    private readonly IReadOnlyList<int> _indices;
    private readonly IReadOnlyList<Guid> _topKeys;
    private readonly IReadOnlyList<RemovedHeading.MovedBlock> _moved;

    internal WrappedSection(
        Guid newHeadingKey, Guid? sectionKey, IReadOnlyList<int> indices, IReadOnlyList<Guid> topKeys, IReadOnlyList<RemovedHeading.MovedBlock> moved)
    {
        NewHeadingKey = newHeadingKey;
        _sectionKey = sectionKey;
        _indices = indices;
        _topKeys = topKeys;
        _moved = moved;
    }

    /// <summary>The key of the new heading N (the element to reveal after the command, with the cursor in its title).</summary>
    public Guid NewHeadingKey { get; }

    /// <summary>
    /// Whether <see cref="RestoreInto"/> can undo the command on <c>book.Page</c>: N is still there below the section
    /// it was put in, its children are only blocks the command moved there, each of them still is its child, and every
    /// other moved block still on the page still has the parent the command left it with (so all of them are still
    /// inside the moved subtrees). Other edits of the page, also of N's title and of the moved blocks' text, do not matter.
    /// </summary>
    public bool CanRestore(Book book)
    {
        var page = book.Page;
        if (Find(page, NewHeadingKey) is not { } heading || heading.Parent?.Key != _sectionKey)
        {
            return false;
        }

        return heading.Children.All(child => _topKeys.Contains(child.Key))
               && _topKeys.All(key => Find(page, key) is { } top && top.Parent == heading)
               && _moved.All(moved => Find(page, moved.Key) is not { } block || block.Parent?.Key == moved.ParentKey);
    }

    /// <summary>
    /// Removes N and puts the moved blocks back into its section at their former indices (counted from N's place now)
    /// and gives the moved headings their former levels; N's title is dropped. A moved block still exactly as the
    /// command left it gets its original lines back; in any other the text stays and only the heading level changes
    /// back. Changed blocks are marked dirty.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="CanRestore"/> is <c>false</c>; the page is unchanged.</exception>
    public void RestoreInto(Book book)
    {
        if (!CanRestore(book))
        {
            throw new InvalidOperationException("The new heading or the blocks below it changed place; the command is not undone.");
        }

        var page = book.Page;
        var heading = Find(page, NewHeadingKey)!;
        var parent = heading.Parent;
        var siblings = parent?.Children ?? page.Roots;
        var tops = _topKeys.Select(key => Find(page, key)!).ToList();
        var unchanged = _moved
            .Where(moved => Find(page, moved.Key) is { } block && block.Lines.SequenceEqual(moved.After))
            .Select(moved => moved.Key)
            .ToHashSet();

        // Take the moved blocks out of N (directly after it, in their order) and remove N.
        var index = IndexOf(siblings, heading);
        for (var i = 0; i < tops.Count; i++)
        {
            page.MoveBlock(tops[i], parent, index + 1 + i);
        }

        page.RemoveBlock(heading);

        // Put each back between the blocks it stood between, the last first: before its target index there are then
        // the blocks that stood before N and the earlier moved blocks (still together at N's former place).
        for (var i = tops.Count - 1; i >= 0; i--)
        {
            var target = Math.Min(index + _indices[i] - _indices[0], siblings.Count - 1);
            if (IndexOf(siblings, tops[i]) != target)
            {
                page.MoveBlock(tops[i], parent, target);
            }
        }

        foreach (var moved in _moved)
        {
            if (Find(page, moved.Key) is { } block)
            {
                moved.Restore(block, unchanged.Contains(moved.Key));
            }
        }

        page.RepairEndings();
    }

    private static Block? Find(Page page, Guid key) => page.AllBlocks().FirstOrDefault(block => block.Key == key);

    private static int IndexOf(IReadOnlyList<Block> siblings, Block block)
    {
        for (var i = 0; i < siblings.Count; i++)
        {
            if (siblings[i] == block)
            {
                return i;
            }
        }

        return siblings.Count;
    }
}
