using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Books;

/// <summary>
/// What <see cref="ManuscriptEditor.RemoveHeading"/> did, so that it can be undone even after later edits of the book
/// (in the style of the link service's undo of a delete): the removed heading block with its original lines, where it
/// was, and the blocks it moved up (by key) with their lines and heading levels before and after the command.
/// <see cref="RestoreInto"/> puts the heading back around the moved blocks; the caller saves the page afterwards.
/// </summary>
public sealed class RemovedHeading
{
    private readonly Block _heading;
    private readonly Guid? _parentKey;
    private readonly int _index;
    private readonly IReadOnlyList<Guid> _childKeys;
    private readonly IReadOnlyList<MovedBlock> _moved;

    internal RemovedHeading(
        Guid reveal, Block heading, Guid? parentKey, int index, IReadOnlyList<Guid> childKeys, IReadOnlyList<MovedBlock> moved)
    {
        Reveal = reveal;
        _heading = heading;
        _parentKey = parentKey;
        _index = index;
        _childKeys = childKeys;
        _moved = moved;
    }

    /// <summary>
    /// The key to reveal after the command: the first moved item, else the element before the heading in the file,
    /// else <see cref="Guid.Empty"/>.
    /// </summary>
    public Guid Reveal { get; }

    /// <summary>
    /// Whether <see cref="RestoreInto"/> can undo the command on <paramref name="page"/>: the heading is not on it again,
    /// its former parent still is, every moved child (by key) still has that parent, and every other moved block still
    /// on the page still has the parent the command left it with (so all of them are still inside the moved subtrees).
    /// Other edits of the page, also of the moved blocks' text, do not matter.
    /// </summary>
    public bool CanRestore(Page page)
    {
        if (_heading.Page is not null || _heading.Parent is not null || Find(page, _heading.Key) is not null)
        {
            return false;
        }

        Block? parent = null;
        if (_parentKey is { } key && (parent = Find(page, key)) is null)
        {
            return false;
        }

        return _childKeys.All(childKey => Find(page, childKey) is { } child && child.Parent == parent)
               && _moved.All(moved => Find(page, moved.Key) is not { } block || block.Parent?.Key == moved.ParentKey);
    }

    /// <summary>
    /// Puts the heading back with its original lines where the moved children are now (without children: at its old
    /// index), moves the children back below it in their order and gives the moved sub-headings their former levels.
    /// A moved block still exactly as the command left it gets its original lines back; in any other the text stays
    /// and only the heading level changes back. Changed blocks are marked dirty.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="CanRestore"/> is <c>false</c>; the page is unchanged.</exception>
    public void RestoreInto(Page page)
    {
        if (!CanRestore(page))
        {
            throw new InvalidOperationException("The removed heading's place or content changed; it is not put back.");
        }

        var parent = _parentKey is { } key ? Find(page, key) : null;
        var siblings = parent?.Children ?? page.Roots;
        var children = _childKeys.Select(childKey => Find(page, childKey)!).ToList();
        var unchanged = _moved
            .Where(moved => Find(page, moved.Key) is { } block && block.Lines.SequenceEqual(moved.After))
            .Select(moved => moved.Key)
            .ToHashSet();

        var index = children.Count > 0 ? children.Min(child => IndexOf(siblings, child)) : Math.Min(_index, siblings.Count);
        if (IndentStyle.ChildIndent(parent) == _heading.Indent)
        {
            page.RestoreBlock(parent, index, _heading);
        }
        else
        {
            // The parent's indentation changed meanwhile: the heading's lines follow the new place.
            page.InsertBlock(parent, index, _heading);
        }

        for (var i = 0; i < children.Count; i++)
        {
            page.MoveBlock(children[i], _heading, i);
        }

        // Lines and levels are restored only inside the restored subtree.
        var restored = Subtree(_heading).ToDictionary(block => block.Key);
        foreach (var moved in _moved)
        {
            if (restored.TryGetValue(moved.Key, out var block))
            {
                moved.Restore(block, unchanged.Contains(moved.Key));
            }
        }

        page.RepairEndings();
    }

    private static Block? Find(Page page, Guid key) => page.AllBlocks().FirstOrDefault(block => block.Key == key);

    /// <summary>The block and all blocks below it, in file order.</summary>
    private static IEnumerable<Block> Subtree(Block block) => block.Children.SelectMany(Subtree).Prepend(block);

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

    /// <summary>The number of <c>#</c> of a heading block, <c>0</c> for any other block.</summary>
    private static int LevelOf(Block block)
    {
        var line = block.Content.Split('\n', 2)[0];
        var level = line.TakeWhile(c => c == '#').Count();
        return level is >= 1 and <= OutlineEditor.MaxLevel && line.Length > level && line[level] == ' ' ? level : 0;
    }

    /// <summary>A block the command moved (a child of the heading or a block below one).</summary>
    /// <param name="Key">The block's key.</param>
    /// <param name="Before">Its lines before the command.</param>
    /// <param name="After">Its lines right after the command.</param>
    /// <param name="Levels">For a heading its level before and after the command; <c>null</c> for any other block.</param>
    /// <param name="ParentKey">The key of its parent right after the command; <c>null</c> for a root block.</param>
    internal sealed record MovedBlock(
        Guid Key, IReadOnlyList<RawLine> Before, IReadOnlyList<RawLine> After, (int Before, int After)? Levels, Guid? ParentKey)
    {
        /// <summary>
        /// Gives <paramref name="block"/>, this block back in its former place, its original lines if it was still exactly
        /// as the command left it (<paramref name="unchanged"/>, checked before the restore moved it); otherwise its text
        /// stays and only a heading level the command changed goes back. A changed block is marked dirty.
        /// </summary>
        internal void Restore(Block block, bool unchanged)
        {
            if (unchanged)
            {
                if (!block.Lines.SequenceEqual(Before))
                {
                    block.RestoreLines(Before, isDirty: true);
                }
            }
            else if (Levels is (int before, int after) && before != after && LevelOf(block) == after)
            {
                block.SetContent(HeadingText.WithLevel(block.Content, after, before));
            }
        }
    }
}
