using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Vaults;

/// <summary>
/// The marked blocks of a note range: <paramref name="HeadKey"/> is the block the range is rooted at,
/// <paramref name="OwnOnly"/> says the range is that block alone (without its sub-points), and <paramref name="Keys"/>
/// are all marked blocks in reading order.
/// </summary>
public sealed record NoteRange(Guid HeadKey, bool OwnOnly, IReadOnlyList<Guid> Keys);

/// <summary>The range levels over the block tree of a note (the note's own block and its sub-points), like the book's range levels.</summary>
public static class NoteRanges
{
    /// <summary>
    /// The range of <paramref name="level"/> around <paramref name="blockKey"/> in the tree of <paramref name="note"/>:
    /// −1 the block alone, 0 the block with its subtree, k &gt; 0 the k-th ancestor with its subtree, at most the
    /// note's own block. A level outside <see cref="LevelBounds"/> is clamped.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="blockKey"/> is not in the tree of <paramref name="note"/>.</exception>
    public static NoteRange Of(NoteBlock note, Guid blockKey, int level)
    {
        var path = PathTo(note, blockKey);
        level = Math.Clamp(level, -1, path.Count - 1);
        if (level < 0)
        {
            return new NoteRange(blockKey, true, [blockKey]);
        }

        var head = path[level];
        return new NoteRange(head.Key, false, [.. Subtree(head).Select(b => b.Key)]);
    }

    /// <summary>The lowest (−1) and highest level for <paramref name="blockKey"/>: its depth below the note's own block.</summary>
    /// <exception cref="ArgumentException"><paramref name="blockKey"/> is not in the tree of <paramref name="note"/>.</exception>
    public static (int Min, int Max) LevelBounds(NoteBlock note, Guid blockKey) => (-1, PathTo(note, blockKey).Count - 1);

    /// <summary>Whether <paramref name="blockKey"/> is the note's own block or one of its sub-points.</summary>
    public static bool Contains(NoteBlock note, Guid blockKey) => Subtree(note.Block).Any(b => b.Key == blockKey);

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
}
