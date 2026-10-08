using NoteEvolution.Core.Format;
using NoteEvolution.Core.Model;

namespace NoteEvolution.Core.Books;

/// <summary>
/// The state of a page before (or after) a structure command: every block with its parent, place and lines, and the
/// file bytes. Undo checks with <see cref="Matches"/> that the page is still what the command left, and puts the
/// state before the command back with <see cref="RestoreInto"/>; the caller saves the page afterwards.
/// </summary>
public sealed class PageStructureSnapshot
{
    private readonly Page _page;
    private readonly byte[] _bytes;
    private readonly List<CapturedBlock> _blocks;

    private PageStructureSnapshot(Page page, byte[] bytes, List<CapturedBlock> blocks)
    {
        _page = page;
        _bytes = bytes;
        _blocks = blocks;
    }

    public static PageStructureSnapshot Capture(Page page) => new(
        page,
        PageSerializer.Serialize(page),
        [.. page.AllBlocks().Select(block => new CapturedBlock(block, block.Parent, [.. block.Lines]))]);

    /// <summary>
    /// <paramref name="page"/> is the captured page and serializes to exactly the captured bytes
    /// (<see cref="PageSerializer.Serialize"/>).
    /// </summary>
    public bool Matches(Page page) => page == _page && PageSerializer.Serialize(page).AsSpan().SequenceEqual(_bytes);

    /// <summary>
    /// Puts every captured block back (parent, index among its siblings, lines) and removes the blocks added since.
    /// Blocks whose lines change are marked dirty, so that the next save writes them.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="page"/> is not the captured page.</exception>
    public void RestoreInto(Page page)
    {
        if (page != _page)
        {
            throw new ArgumentException("The snapshot was captured from another page.", nameof(page));
        }

        foreach (var captured in _blocks.Where(captured => !captured.Block.Lines.SequenceEqual(captured.Lines)))
        {
            captured.Block.RestoreLines(captured.Lines, isDirty: true);
        }

        page.RestoreTree([.. _blocks.Select(captured => (captured.Block, captured.Parent))]);
    }

    private sealed record CapturedBlock(Block Block, Block? Parent, IReadOnlyList<RawLine> Lines);
}
