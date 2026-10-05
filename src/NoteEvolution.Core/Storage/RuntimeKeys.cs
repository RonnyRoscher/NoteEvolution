using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Storage;

/// <summary>Keeps <see cref="Block.Key"/> stable when a page is replaced by a newly read version of its file.</summary>
public static class RuntimeKeys
{
    /// <summary>
    /// Gives each block of <paramref name="to"/> the key of its counterpart in <paramref name="from"/>: the block with
    /// the same <c>id::</c>; else the block at the same tree path with the same lines; else the first free block with
    /// the same lines elsewhere (a block shifted by an insertion above it). Lines are compared without indentation,
    /// line endings and trailing blank lines. Blocks without a counterpart keep their new keys.
    /// </summary>
    public static void Carry(Page from, Page to)
    {
        var sources = BlockMatcher.WithPaths(from).Select(b => new BlockMatcher.Source(b.Block, b.Block.Lines, b.Path)).ToList();
        foreach (var (source, target) in BlockMatcher.Match(sources, to, byPathAlone: false))
        {
            target.AssignKey(source.Key);
        }
    }
}
