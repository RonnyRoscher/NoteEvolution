using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Storage;

/// <summary>A block changed locally (not saved yet) whose file version was changed or deleted externally.</summary>
/// <param name="Local">
/// The local block. For <see cref="LocalRemoved"/> it is the removed block, detached, with its last local lines.
/// </param>
/// <param name="External">The block in the external version; <c>null</c> if it is not there (deleted, or unreadable file).</param>
public sealed record BlockConflict(Block Local, Block? External)
{
    /// <summary>The block was removed locally; <see cref="ConflictChoice.Mine"/> keeps it removed.</summary>
    public bool LocalRemoved { get; init; }
}

/// <summary>Finds the blocks whose unsaved local changes collide with an external version of the file.</summary>
public static class ConflictDetector
{
    /// <summary>
    /// Compares the blocks of <paramref name="local"/>'s last saved state (see <see cref="Page.MarkSaved"/>) with
    /// <paramref name="external"/>. Blocks are paired by <c>id::</c>, else by tree path with the same text, else (only
    /// blocks unchanged locally) by the same text elsewhere, else by their place among unambiguously paired siblings: a
    /// locally changed or removed block takes the one block there with its saved text (an insertion next to it), else
    /// the block at its place (the same block, changed externally). A locally changed or removed block whose place is
    /// ambiguous (several blocks there with its saved text, or differing counts) counts as deleted
    /// externally, so that no other external block is overwritten or removed for it. A conflict is a block
    /// <list type="bullet">
    /// <item>changed locally whose external version differs from the saved one (and from the local one), or is missing;</item>
    /// <item>removed locally whose external version differs from the saved one.</item>
    /// </list>
    /// Blocks new since the last save never conflict. Texts are compared without indentation, line endings and
    /// trailing blank lines.
    /// </summary>
    /// <returns>The conflicts in the saved block order.</returns>
    public static IReadOnlyList<BlockConflict> Detect(Page local, Page external) =>
        [.. Compare(local, external).Where(e => e.IsConflict).Select(e => e.ToConflict())];

    /// <summary>
    /// Every unsaved local block change without an external counterpart: changed and new blocks in file order,
    /// then removed ones. Used when there is no usable external version (file deleted or unreadable).
    /// </summary>
    internal static IReadOnlyList<BlockConflict> LocalChanges(Page local)
    {
        var saved = local.Saved.Blocks.Select(b => b.Block).ToHashSet(BlockMatcher.ByReference);
        var changed = local.AllBlocks()
            .Where(b => !saved.Contains(b) || !BlockMatcher.SameText(b.Lines, b.BaseLines))
            .Select(b => new BlockConflict(b, null));
        var removed = local.Saved.Blocks
            .Where(b => b.Block.Page != local)
            .Select(b => new BlockConflict(b.Block, null) { LocalRemoved = true });
        return [.. changed, .. removed];
    }

    /// <summary>One entry per block of <paramref name="local"/>'s saved state, in that order.</summary>
    internal static IReadOnlyList<SavedEntry> Compare(Page local, Page external)
    {
        var sources = local.Saved.Blocks
            .Select(b => new BlockMatcher.Source(
                b.Block,
                b.Parent?.Block,
                b.Block.BaseLines,
                b.Path,
                Strict: b.Block.Page != local || !BlockMatcher.SameText(b.Block.Lines, b.Block.BaseLines)))
            .ToList();
        var pairs = BlockMatcher.Match(sources, external, byPosition: true);
        return
        [
            .. local.Saved.Blocks.Select(saved =>
            {
                var block = saved.Block;
                var present = block.Page == local;
                var counterpart = pairs.GetValueOrDefault(block);
                return new SavedEntry(
                    saved,
                    present,
                    present && !BlockMatcher.SameText(block.Lines, block.BaseLines),
                    counterpart,
                    counterpart is not null && !BlockMatcher.SameText(counterpart.Lines, block.BaseLines));
            }),
        ];
    }

    /// <summary>A block of the saved state compared on both sides.</summary>
    /// <param name="Saved">The block and its place in the saved state.</param>
    /// <param name="Present">The block is still on the local page (otherwise it was removed locally).</param>
    /// <param name="LocalChanged">Its local text differs from the saved one.</param>
    /// <param name="External">Its counterpart in the external version, if any.</param>
    /// <param name="ExternalChanged">The counterpart's text differs from the saved one.</param>
    internal sealed record SavedEntry(Page.SavedBlock Saved, bool Present, bool LocalChanged, Block? External, bool ExternalChanged)
    {
        public Block Local => Saved.Block;

        public bool IsConflict => Present
            ? LocalChanged && (External is null || ExternalChanged && !BlockMatcher.SameText(Local.Lines, External.Lines))
            : External is not null && ExternalChanged;

        public BlockConflict ToConflict() => new(Local, External) { LocalRemoved = !Present };
    }
}
