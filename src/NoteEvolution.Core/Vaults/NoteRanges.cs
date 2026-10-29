using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Vaults;

/// <summary>
/// The marked blocks of a note range: <paramref name="HeadKey"/> is the block the range is rooted at,
/// <paramref name="OwnOnly"/> says the range is that block alone (without its sub-points), and <paramref name="Keys"/>
/// are all marked blocks in reading order.
/// </summary>
public sealed record NoteRange(Guid HeadKey, bool OwnOnly, IReadOnlyList<Guid> Keys);

/// <summary>
/// The range levels over the block tree of a note (the note's own block and its sub-points), like the book's range levels.
/// With <c>hideUsed</c> ("Verwendete ausblenden") only the shown sub-points count (<see cref="NoteBlock.ShownChildren"/>).
/// </summary>
public static class NoteRanges
{
    /// <summary>
    /// The range of <paramref name="level"/> around <paramref name="blockKey"/> in the tree of <paramref name="note"/>:
    /// −1 the block alone, 0 the block with its (shown) subtree, k &gt; 0 the k-th ancestor with its subtree, at most the
    /// note's own block. A level outside <see cref="LevelBounds"/> is clamped.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="blockKey"/> is not in the tree of <paramref name="note"/>.</exception>
    public static NoteRange Of(NoteBlock note, Guid blockKey, int level, bool hideUsed = false)
    {
        var path = PathTo(note, blockKey);
        level = Math.Clamp(level, MinLevel(path[0], hideUsed), path.Count - 1);
        if (level < 0)
        {
            return new NoteRange(blockKey, true, [blockKey]);
        }

        var head = path[level];
        return new NoteRange(head.Key, false, [.. Shown(head, hideUsed).Select(b => b.Key)]);
    }

    /// <summary>
    /// The lowest and highest level for <paramref name="blockKey"/>: −1 (the block alone) for a block with (shown)
    /// sub-points, else 0, since alone it is the same as with its subtree; the highest is its depth below the note's own block.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="blockKey"/> is not in the tree of <paramref name="note"/>.</exception>
    public static (int Min, int Max) LevelBounds(NoteBlock note, Guid blockKey, bool hideUsed = false)
    {
        var path = PathTo(note, blockKey);
        return (MinLevel(path[0], hideUsed), path.Count - 1);
    }

    /// <summary>Whether <paramref name="blockKey"/> is the note's own block or one of its sub-points.</summary>
    public static bool Contains(NoteBlock note, Guid blockKey) => Subtree(note.Block).Any(b => b.Key == blockKey);

    private static int MinLevel(Block block, bool hideUsed) => NoteBlock.ShownChildren(block, hideUsed).Any() ? -1 : 0;

    /// <summary>The blocks from <paramref name="blockKey"/> up to the note's own block, the block first.</summary>
    private static List<Block> PathTo(NoteBlock note, Guid blockKey)
    {
        var target = Subtree(note.Block).FirstOrDefault(b => b.Key == blockKey)
                     ?? throw new ArgumentException("The block is not part of the note.", nameof(blockKey));
        var path = new List<Block>();
        for (var block = target; ; block = block.Parent!)
        {
            path.Add(block);
            if (ReferenceEquals(block, note.Block))
            {
                return path;
            }
        }
    }

    private static IEnumerable<Block> Subtree(Block block) => block.Children.SelectMany(Subtree).Prepend(block);

    /// <summary><paramref name="block"/> with its shown sub-points, in reading order.</summary>
    private static IEnumerable<Block> Shown(Block block, bool hideUsed) =>
        NoteBlock.ShownChildren(block, hideUsed).SelectMany(child => Shown(child, hideUsed)).Prepend(block);
}
